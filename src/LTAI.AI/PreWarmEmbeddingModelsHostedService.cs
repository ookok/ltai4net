// Copyright (c) LTAI. All rights reserved.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LTAI.Core.Configuration;

namespace LTAI.AI;

/// <summary>
/// P14.12: Opt-in background pre-warm of all known embedding models.
/// Downloads models (if not already on disk) and warms up the ONNX runtime
/// so the first embedding call doesn't block on model loading.
///
/// Only runs when LTAI:Embedding:PreWarmAllModels=true (default false).
/// No-ops when remote API key is in use (DefaultDisabled) or no models directory.
/// Runs in the background with a 30s timeout — never blocks startup.
/// </summary>
public sealed class PreWarmEmbeddingModelsHostedService : IHostedService
{
    private readonly IOptions<LTAIOptions> _options;
    private readonly ILogger<PreWarmEmbeddingModelsHostedService> _logger;
    private readonly IHttpClientFactory? _httpFactory;
    private readonly CancellationTokenSource _cts = new();

    public PreWarmEmbeddingModelsHostedService(
        IOptions<LTAIOptions> options,
        ILogger<PreWarmEmbeddingModelsHostedService>? logger = null,
        IHttpClientFactory? httpFactory = null)
    {
        _options = options;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PreWarmEmbeddingModelsHostedService>.Instance;
        _httpFactory = httpFactory;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (LocalEmbedder.DefaultDisabled)
        {
            _logger.LogDebug("PreWarmEmbeddingModels: skipped (remote API in use)");
            return Task.CompletedTask;
        }

        if (!_options.Value.Embedding.PreWarmAllModels)
        {
            _logger.LogDebug("PreWarmEmbeddingModels: skipped (LTAI:Embedding:PreWarmAllModels=false — not opted in)");
            return Task.CompletedTask;
        }

        if (LocalEmbedder.BaseModelsDirectory == null)
        {
            _logger.LogDebug("PreWarmEmbeddingModels: skipped (no models directory)");
            return Task.CompletedTask;
        }

        // Fire-and-forget with timeout: user-facing startup is not blocked
        _ = PreWarmAllAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    private async Task PreWarmAllAsync(CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            _logger.LogInformation("PreWarmEmbeddingModels: starting background download...");

            var http = _httpFactory?.CreateClient() ?? new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromMinutes(5) };
            var disposeHttp = _httpFactory == null;

            try
            {
                // Try to download and warm up each model
                foreach (var (name, _) in LocalEmbedder.KnownModels)
                {
                    if (timeoutCts.Token.IsCancellationRequested) break;

                    var baseDir = LocalEmbedder.BaseModelsDirectory;
                    if (baseDir == null) break;

                    var modelDir = Path.Combine(baseDir, name);
                    var modelFile = Path.Combine(modelDir, "model.onnx");
                    var quantFile = Path.Combine(modelDir, "model.int8.onnx");
                    var vocabFile = Path.Combine(modelDir, "vocab.txt");
                    var hasModel = File.Exists(modelFile) || File.Exists(quantFile);
                    var hasVocab = File.Exists(vocabFile);

                    if (!hasModel || !hasVocab)
                    {
                        _logger.LogInformation("PreWarmEmbeddingModels: downloading {Model}...", name);
                        await LocalEmbedder.DownloadModelStaticAsync(name, http)
                            .WaitAsync(timeoutCts.Token)
                            .ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                if (disposeHttp) http.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("PreWarmEmbeddingModels: timed out (30s) — models may not be ready yet");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PreWarmEmbeddingModels: download failed");
        }
    }
}
