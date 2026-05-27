using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

/// <summary>
/// Shares Skills (.md files) and subspace projections across federated nodes.
/// Skills are the only distributable artifact for code; subspace projections
/// enable efficient weight/knowledge transfer via shared low-rank subspaces
/// (Universal Weight Subspace Hypothesis, Kaushik et al. 2025).
/// </summary>
public sealed class FederatedLearningService : BackgroundService
{
    private readonly IFederatedTransport? _transport;
    private readonly ILogger<FederatedLearningService> _logger;
    private readonly string _nodeId;
    private readonly string _skillsRoot;
    private FileSystemWatcher? _watcher;
    private readonly HashSet<string> _pendingShares = new();
    private readonly WeightSubspaceAnalyzer? _subspaceAnalyzer;
    private readonly ConcurrentDictionary<string, float[]> _sharedProjections = new();

    public FederatedLearningService(
        IFederatedTransport? transport,
        ILogger<FederatedLearningService>? logger = null,
        string? skillsRoot = null,
        WeightSubspaceAnalyzer? subspaceAnalyzer = null)
    {
        _transport = transport;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<FederatedLearningService>.Instance;
        _nodeId = _transport?.PeerId ?? Guid.NewGuid().ToString("N")[..8];
        _skillsRoot = skillsRoot ?? Path.Combine(AppContext.BaseDirectory, "skills");
        _subspaceAnalyzer = subspaceAnalyzer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_transport == null)
        {
            _logger.LogInformation("FederatedLearning: disabled, no transport");
            return;
        }

        _logger.LogInformation("FederatedLearning: sharing Skills from {Root}", _skillsRoot);

        StartSkillWatcher();

        await foreach (var (type, payload, sourceId) in _transport.ReceiveMessagesAsync(stoppingToken))
        {
            if (type == "skill_share" && sourceId != _nodeId)
            {
                await ReceiveSkillAsync(payload, stoppingToken).ConfigureAwait(false);
            }
            else if (type == "subspace_share" && sourceId != _nodeId)
            {
                await ReceiveSubspaceAsync(payload, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private void StartSkillWatcher()
    {
        if (!Directory.Exists(_skillsRoot)) return;

        _watcher = new FileSystemWatcher(_skillsRoot, "*.md")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
        };

        _watcher.Changed += OnSkillChanged;
        _watcher.Created += OnSkillChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnSkillChanged(object sender, FileSystemEventArgs e)
    {
        var name = Path.GetFileNameWithoutExtension(e.Name);
        if (name.EndsWith(".meta")) return;

        lock (_pendingShares)
        {
            _pendingShares.Add(e.FullPath);
        }
    }

    public async Task SharePendingSkillsAsync(CancellationToken ct = default)
    {
        if (_transport == null) return;

        List<string> toShare;
        lock (_pendingShares)
        {
            toShare = _pendingShares.ToList();
            _pendingShares.Clear();
        }

        foreach (var path in toShare)
        {
            if (!File.Exists(path)) continue;

            try
            {
                var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                var payload = JsonSerializer.Serialize(new { file = Path.GetFileName(path), content });
                await _transport.SendMessageAsync("skill_share", payload, ct).ConfigureAwait(false);
                _logger.LogInformation("FederatedLearning: shared skill {File}", Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FederatedLearning: failed to share {File}", path);
            }
        }
    }

    private async Task ReceiveSkillAsync(string payload, CancellationToken ct)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<SkillShareMessage>(payload);
            if (msg == null || string.IsNullOrEmpty(msg.File) || string.IsNullOrEmpty(msg.Content))
                return;

            var destDir = DetermineLayerDir(msg.Content);
            var destPath = Path.Combine(_skillsRoot, destDir, msg.File);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            if (File.Exists(destPath))
            {
                var existing = await File.ReadAllTextAsync(destPath, ct).ConfigureAwait(false);
                if (existing == msg.Content) return;
            }

            await File.WriteAllTextAsync(destPath, msg.Content, ct).ConfigureAwait(false);
            _logger.LogInformation("FederatedLearning: received skill {File}", msg.File);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FederatedLearning: failed to receive skill");
        }
    }

    private static string DetermineLayerDir(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            if (line.TrimStart().StartsWith("layer:"))
            {
                var layer = line.Split(':')[1].Trim().ToLowerInvariant();
                return layer switch
                {
                    "0" or "l0" => "l0_atomic",
                    "1" or "l1" => "l1_task",
                    "2" or "l2" => "l2_workflow",
                    "3" or "l3" => "l3_domain",
                    "4" or "l4" => "l4_meta",
                    _ => "l1_task"
                };
            }
        }
        return "l1_task";
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }

    public async Task ShareSubspaceAsync(string realm, float[] projection, CancellationToken ct = default)
    {
        if (_transport == null) return;

        try
        {
            var compressed = CompressProjection(projection);
            var payload = JsonSerializer.Serialize(new SubspaceShareMessage
            {
                Realm = realm,
                ProjectionBase64 = Convert.ToBase64String(compressed),
                Dim = projection.Length,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            await _transport.SendMessageAsync("subspace_share", payload, ct).ConfigureAwait(false);
            _sharedProjections[realm] = projection;
            _logger.LogInformation("FederatedLearning: shared subspace projection realm={Realm} dim={Dim}", realm, projection.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FederatedLearning: failed to share subspace {Realm}", realm);
        }
    }

    private async Task ReceiveSubspaceAsync(string payload, CancellationToken ct)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<SubspaceShareMessage>(payload);
            if (msg == null || string.IsNullOrEmpty(msg.Realm) || msg.Dim <= 0)
                return;

            var bytes = Convert.FromBase64String(msg.ProjectionBase64 ?? "");
            var projection = DecompressProjection(bytes, msg.Dim);

            if (_subspaceAnalyzer != null)
            {
                _subspaceAnalyzer.Analyze(new[] { projection }, $"fed_{msg.Realm}_{_nodeId}");
            }

            _sharedProjections[msg.Realm] = projection;
            _logger.LogInformation("FederatedLearning: received subspace projection realm={Realm} dim={Dim}", msg.Realm, msg.Dim);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FederatedLearning: failed to receive subspace projection");
        }
    }

    public float[]? GetSharedProjection(string realm)
    {
        return _sharedProjections.GetValueOrDefault(realm);
    }

    private static byte[] CompressProjection(float[] projection)
    {
        var bytes = new byte[projection.Length * 2];
        for (int i = 0; i < projection.Length; i++)
        {
            var half = (Half)projection[i];
            var raw = BitConverter.HalfToInt16Bits(half);
            bytes[i * 2] = (byte)(raw & 0xFF);
            bytes[i * 2 + 1] = (byte)(raw >> 8);
        }
        return bytes;
    }

    private static float[] DecompressProjection(byte[] data, int dim)
    {
        var projection = new float[dim];
        for (int i = 0; i < Math.Min(dim, data.Length / 2); i++)
        {
            var raw = (short)(data[i * 2] | (data[i * 2 + 1] << 8));
            projection[i] = (float)BitConverter.Int16BitsToHalf(raw);
        }
        return projection;
    }

    private sealed record SkillShareMessage
    {
        public string? File { get; init; }
        public string? Content { get; init; }
    }

    private sealed record SubspaceShareMessage
    {
        public string? Realm { get; init; }
        public string? ProjectionBase64 { get; init; }
        public int Dim { get; init; }
        public long Timestamp { get; init; }
    }
}

public interface IFederatedTransport
{
    string PeerId { get; }
    Task SendMessageAsync(string type, string payload, CancellationToken ct = default);
    IAsyncEnumerable<(string Type, string Payload, string SourceId)> ReceiveMessagesAsync(CancellationToken ct = default);
}
