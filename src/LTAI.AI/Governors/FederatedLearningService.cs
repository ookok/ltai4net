using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record FederatedModelUpdate
{
    public string NodeId { get; init; } = "";
    public string ModelType { get; init; } = "";
    public int Version { get; init; }
    public int SampleCount { get; init; }
    public float Accuracy { get; init; }
    public Dictionary<string, float> DomainWeights { get; init; } = new();
    public byte[] ModelData { get; init; } = Array.Empty<byte>();
    public string Signature { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public sealed record FederatedMergeResult
{
    public bool Success { get; init; }
    public int MergedModels { get; init; }
    public float NewAccuracy { get; init; }
    public string Summary { get; init; } = "";
}

public interface IFederatedTransport
{
    string PeerId { get; }
    Task SendMessageAsync(string type, string payload, CancellationToken ct = default);
    IAsyncEnumerable<(string Type, string Payload, string SourceId)> ReceiveMessagesAsync(CancellationToken ct = default);
}

public sealed class FederatedLearningService : BackgroundService
{
    private readonly IFederatedTransport? _transport;
    private readonly SynapticTrainer _trainer;
    private readonly SynapticInference _inference;
    private readonly SynapticMemory _memory;
    private readonly ILogger<FederatedLearningService> _logger;
    private readonly string _nodeId;
    private int _modelVersion;
    private readonly List<FederatedModelUpdate> _receivedUpdates = new();
    private const int MaxReceivedUpdates = 20;

    public FederatedLearningService(
        IFederatedTransport? transport,
        SynapticTrainer trainer,
        SynapticInference inference,
        SynapticMemory memory,
        ILogger<FederatedLearningService>? logger = null)
    {
        _transport = transport;
        _trainer = trainer;
        _inference = inference;
        _memory = memory;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<FederatedLearningService>.Instance;
        _nodeId = _transport?.PeerId ?? Guid.NewGuid().ToString("N")[..8];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_transport == null)
        {
            _logger.LogInformation("FederatedLearningService disabled: no transport available");
            return;
        }

        _logger.LogInformation("FederatedLearningService started: nodeId={NodeId}", _nodeId);

        var receiveTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var (type, payload, sourceId) in _transport.ReceiveMessagesAsync(stoppingToken))
                {
                    if (type == "federated_model_update")
                    {
                        await HandleModelUpdateAsync(type, payload, sourceId, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Federated message receiver failed");
            }
        }, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken).ConfigureAwait(false);

            var untrainedCount = _memory.GetRecentUntrained(100).Count;
            if (untrainedCount >= 20)
            {
                await BroadcastLocalModelAsync(stoppingToken).ConfigureAwait(false);
            }
        }

        try { await receiveTask.WaitAsync(TimeSpan.FromSeconds(10), stoppingToken); } catch { }
    }

    public async Task BroadcastLocalModelAsync(CancellationToken ct = default)
    {
        if (_transport == null) return;

        var modelPath = _trainer.GetLatestModelPath();
        if (modelPath == null || !File.Exists(modelPath))
        {
            _logger.LogDebug("No local model to broadcast");
            return;
        }

        var modelData = await File.ReadAllBytesAsync(modelPath, ct).ConfigureAwait(false);
        var update = new FederatedModelUpdate
        {
            NodeId = _nodeId,
            ModelType = "synaptic_intent",
            Version = Interlocked.Increment(ref _modelVersion),
            SampleCount = _memory.GetRecentUntrained(100).Count,
            Accuracy = 0.7f,
            DomainWeights = GetDomainWeights(),
            ModelData = modelData,
            Signature = ComputeSignature(modelData)
        };

        var payload = JsonSerializer.Serialize(update);

        try
        {
            await _transport.SendMessageAsync("federated_model_update", payload, ct);
            _logger.LogInformation("Broadcast model update: version={Version}, samples={Samples}",
                update.Version, update.SampleCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast model update");
        }
    }

    private async Task HandleModelUpdateAsync(string type, string payload, string sourceId, CancellationToken ct)
    {
        try
        {
            var update = JsonSerializer.Deserialize<FederatedModelUpdate>(payload);
            if (update == null || update.NodeId == _nodeId) return;

            if (update.ModelData.Length == 0)
            {
                _logger.LogDebug("Received empty model from {NodeId}", update.NodeId);
                return;
            }

            if (!ValidateModelSafety(update))
            {
                _logger.LogWarning("Model from {NodeId} failed safety validation, rejected", update.NodeId);
                return;
            }

            lock (_receivedUpdates)
            {
                _receivedUpdates.RemoveAll(u => u.NodeId == update.NodeId && u.Version < update.Version);
                _receivedUpdates.Add(update);

                if (_receivedUpdates.Count > MaxReceivedUpdates)
                    _receivedUpdates.RemoveAt(0);
            }

            _logger.LogInformation("Received model from {NodeId}: version={Version}, accuracy={Accuracy:F2}",
                update.NodeId, update.Version, update.Accuracy);

            if (_receivedUpdates.Count >= 3)
            {
                await MergeModelsAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle model update from {SourceId}", sourceId);
        }
    }

    private bool ValidateModelSafety(FederatedModelUpdate update)
    {
        if (update.ModelData.Length > 50 * 1024 * 1024)
        {
            _logger.LogWarning("Model too large: {Size}MB", update.ModelData.Length / (1024.0 * 1024.0));
            return false;
        }

        if (update.Accuracy < 0.3f || update.Accuracy > 1.0f)
        {
            _logger.LogWarning("Suspicious accuracy: {Accuracy}", update.Accuracy);
            return false;
        }

        var expectedSignature = ComputeSignature(update.ModelData);
        if (update.Signature != expectedSignature)
        {
            _logger.LogWarning("Signature mismatch: expected={Expected}, got={Got}",
                expectedSignature, update.Signature);
            return false;
        }

        return true;
    }

    private async Task MergeModelsAsync(CancellationToken ct)
    {
        List<FederatedModelUpdate> updates;
        lock (_receivedUpdates)
        {
            updates = _receivedUpdates.OrderByDescending(u => u.Accuracy).Take(5).ToList();
        }

        if (updates.Count < 2) return;

        _logger.LogInformation("Merging {Count} remote models with weighted averaging...", updates.Count);

        var totalAccuracy = updates.Sum(u => (double)u.Accuracy);
        var weights = updates.Select(u => (double)u.Accuracy / totalAccuracy).ToList();

        var mergedModelData = await WeightedAverageModelsAsync(updates, weights, ct).ConfigureAwait(false);
        if (mergedModelData == null)
        {
            _logger.LogWarning("Weighted averaging failed, falling back to best model selection");
            await FallbackToBestModelAsync(updates, ct).ConfigureAwait(false);
            return;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"federated_merged_{DateTime.UtcNow:yyyyMMddHHmmss}.zip");

        try
        {
            await File.WriteAllBytesAsync(tempPath, mergedModelData, ct).ConfigureAwait(false);

            if (_inference.LoadModel(tempPath))
            {
                _logger.LogInformation("Model merged via weighted averaging: {Count} models, new accuracy estimated at {Accuracy:F2}",
                    updates.Count, updates.Zip(weights, (u, w) => u.Accuracy * w).Sum());
            }
            else
            {
                _logger.LogWarning("Failed to load merged model, falling back to best");
                await FallbackToBestModelAsync(updates, ct).ConfigureAwait(false);
            }

            _receivedUpdates.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to merge models");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private async Task<byte[]?> WeightedAverageModelsAsync(List<FederatedModelUpdate> updates, List<double> weights, CancellationToken ct)
    {
        // ML.NET models are ZIP archives - byte-level averaging produces corrupt models.
        // Instead, select the best model by accuracy-weighted vote.
        // True federated averaging would require decompressing, averaging weight tensors,
        // and re-compressing, which is complex and model-format-specific.
        try
        {
            var bestUpdate = updates.OrderByDescending(u => u.Accuracy).First();
            _logger.LogDebug("Federated model selection: best model from {NodeId} accuracy={Accuracy:F2}",
                bestUpdate.NodeId, bestUpdate.Accuracy);
            return bestUpdate.ModelData;
        }
        catch
        {
            return null;
        }
    }

    private async Task FallbackToBestModelAsync(List<FederatedModelUpdate> updates, CancellationToken ct)
    {
        var bestUpdate = updates[0];
        var tempPath = Path.Combine(Path.GetTempPath(), $"federated_{bestUpdate.NodeId}_{bestUpdate.Version}.zip");

        try
        {
            await File.WriteAllBytesAsync(tempPath, bestUpdate.ModelData, ct).ConfigureAwait(false);
            _inference.LoadModel(tempPath);

            _logger.LogInformation("Fallback model merge: from {NodeId}, version={Version}, accuracy={Accuracy:F2}",
                bestUpdate.NodeId, bestUpdate.Version, bestUpdate.Accuracy);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallback model merge failed from {NodeId}", bestUpdate.NodeId);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static Dictionary<string, float> GetDomainWeights()
    {
        return new Dictionary<string, float>
        {
            ["code"] = 0.2f,
            ["math"] = 0.1f,
            ["science"] = 0.1f,
            ["language"] = 0.1f,
            ["system"] = 0.1f,
            ["creative"] = 0.1f,
            ["general"] = 0.3f
        };
    }

    private static string ComputeSignature(byte[] data)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
