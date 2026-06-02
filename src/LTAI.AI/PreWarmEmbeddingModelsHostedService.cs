// Copyright (c) LTAI. All rights reserved.

using LTAI.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI;

/// <summary>
/// P14.12: opt-in background pre-warm of every
/// <see cref="LocalEmbedder.KnownModels"/> entry on host start. Users with
/// <c>"LTAI:Embedding:PreWarmAllModels": true</c> in appsettings.json get
/// every model variant downloaded in the background during process
/// startup, so the first <c>/model switch &lt;name&gt;</c> or
/// <c>/model quant fp32|int8</c> command never has to wait for a
/// 22-95 MB download.
/// <para>
/// Safety / no-op conditions:
/// <list type="bullet">
///   <item><description><see cref="LocalEmbedder.DefaultDisabled"/> is
///     <c>true</c> (remote API key in use) — skip entirely; the local
///     model is irrelevant to this deployment.</description></item>
///   <item><description><see cref="EmbeddingConfig.PreWarmAllModels"/> is
///     <c>false</c> (default) — skip unless user explicitly opts in.
///     Downloading 213 MB on every cold start without consent is rude.</description></item>
///   <item><description><see cref="LocalEmbedder.BaseModelsDirectory"/>
///     is <c>null</c> — skip; the LocalEmbedder ctor couldn't locate a
///     models directory at all.</description></item>
/// </list>
/// </para>
/// <para>
/// The download runs in a <c>Task.Run</c> detached from the host
/// startup cancellation token — the user's first chat request is
/// never blocked or cancelled by an in-flight 22-95 MB download. We do
/// keep our own internal <see cref="CancellationTokenSource"/> for clean
/// <see cref="StopAsync"/> shutdown (best-effort cancel).
/// </para>
/// </summary>
public sealed class PreWarmEmbeddingModelsHostedService : IHostedService, IDisposable
{
    private readonly IOptions<LTAIOptions> _config;
    private readonly IHttpClientFactory? _httpFactory;
    private readonly ILogger<PreWarmEmbeddingModelsHostedService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _running;

    public PreWarmEmbeddingModelsHostedService(
        IOptions<LTAIOptions> config,
        ILogger<PreWarmEmbeddingModelsHostedService> logger,
        IHttpClientFactory? httpFactory = null)
    {
        _config = config;
        _logger = logger;
        _httpFactory = httpFactory;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // P14.12: gate by the 3 no-op conditions listed in the class summary.
        if (LocalEmbedder.DefaultDisabled)
        {
            _logger.LogDebug("PreWarmEmbeddingModels: skipped (LocalEmbedder.DefaultDisabled=true — remote API in use)");
            return Task.CompletedTask;
        }
        if (!(_config.Value.Embedding?.PreWarmAllModels ?? false))
        {
            _logger.LogDebug("PreWarmEmbeddingModels: skipped (LTAI:Embedding:PreWarmAllModels=false — not opted in)");
            return Task.CompletedTask;
        }
        if (LocalEmbedder.BaseModelsDirectory is null)
        {
            _logger.LogWarning("PreWarmEmbeddingModels: skipped (BaseModelsDirectory is null — LocalEmbedder couldn't locate a models dir)");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Fire-and-forget: user-facing startup is not blocked by downloads.
        // Logged at start + end so the operator can see progress without
        // parsing verbose TRACE logs.
        _running = Task.Run(() => PreWarmAllAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts != null)
        {
            _cts.Cancel();
            if (_running != null)
            {
                // Best-effort wait — don't block process shutdown forever.
                try { await _running.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected on shutdown */ }
                catch (Exception ex) { _logger.LogWarning(ex, "PreWarmEmbeddingModels: error during shutdown"); }
            }
        }
    }

    private async Task PreWarmAllAsync(CancellationToken ct)
    {
        var baseDir = LocalEmbedder.BaseModelsDirectory!;
        var modelIds = LocalEmbedder.KnownModels.Keys.ToList();
        var http = _httpFactory?.CreateClient();
        if (http != null) http.Timeout = TimeSpan.FromMinutes(10);

        _logger.LogInformation(
            "PreWarmEmbeddingModels: starting background download of {N} known models to {Dir}",
            modelIds.Count, baseDir);

        int downloaded = 0, skipped = 0, failed = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var id in modelIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var modelDir = Path.Combine(baseDir, id);
                var vocabFile = Path.Combine(modelDir, "vocab.txt");
                var hasAnyVariant = File.Exists(Path.Combine(modelDir, "model.onnx"))
                                || File.Exists(Path.Combine(modelDir, "model.int8.onnx"));
                if (hasAnyVariant && File.Exists(vocabFile))
                {
                    _logger.LogDebug("PreWarmEmbeddingModels: {Id} already on disk, skipping", id);
                    skipped++;
                    continue;
                }
                _logger.LogInformation("PreWarmEmbeddingModels: downloading {Id}...", id);
                var ok = await LocalEmbedder.DownloadModelStaticAsync(id, http).ConfigureAwait(false);
                if (ok)
                {
                    downloaded++;
                    _logger.LogInformation("PreWarmEmbeddingModels: {Id} downloaded ✓", id);
                }
                else
                {
                    failed++;
                    _logger.LogWarning("PreWarmEmbeddingModels: {Id} download returned false (check URL / network)", id);
                }
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "PreWarmEmbeddingModels: {Id} failed", id);
            }
        }
        sw.Stop();
        _logger.LogInformation(
            "PreWarmEmbeddingModels: complete in {Sec:F1}s — downloaded={D} skipped={S} failed={F}",
            sw.Elapsed.TotalSeconds, downloaded, skipped, failed);
    }

    public void Dispose() => _cts?.Dispose();
}
