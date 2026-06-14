using LTAI.Agent.Vector;
using LTAI.Agent.Memory;
using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Services;

/// <summary>
/// Background warmup service that pre-initializes expensive singletons
/// before the first user request arrives. Eliminates cold-start TTFT penalties.
///
/// Warmup targets:
///   1. ToolRegistry.InitializeAsync — BM25 index + ONNX tool embeddings (55-215ms)
///   2. KbGraph.IsKnowledgeQuery centroids — FastEmb centroid computation (10-50ms)
///   3. AgentToolStore tool population — resolves LTAI-Chat to trigger AgentBuilder
///
/// Total cold-start penalty eliminated: ~65-265ms from first user request.
/// </summary>
public sealed class WarmupService : IHostedService
{
    private readonly IServiceProvider _sp;
    private readonly KbGraph _kbGraph;
    private readonly EmbeddingClient _embedder;
    private readonly PalaceStore? _palaceStore;
    private readonly ToolEmbeddingCache? _toolCache;
    private readonly ILogger<WarmupService> _logger;

    public WarmupService(
        IServiceProvider sp,
        KbGraph kbGraph,
        EmbeddingClient embedder,
        PalaceStore? palaceStore = null,
        ToolEmbeddingCache? toolCache = null,
        ILogger<WarmupService>? logger = null)
    {
        _sp = sp;
        _kbGraph = kbGraph;
        _embedder = embedder;
        _palaceStore = palaceStore;
        _toolCache = toolCache;
        _logger = logger ?? NullLogger<WarmupService>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("WarmupService: scheduling background warmup...");

        _ = Task.Run(async () =>
        {
            try
            {
                await WarmupToolRegistryAsync(cancellationToken).ConfigureAwait(false);
                await WarmupKbGraphAsync(cancellationToken).ConfigureAwait(false);
                await WarmupPalaceStoreAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("WarmupService: complete");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WarmupService: warmup failed (will warm on first request)");
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task WarmupToolRegistryAsync(CancellationToken ct)
    {
        if (ToolRegistry.IsInitialized)
        {
            _logger.LogDebug("WarmupService: ToolRegistry already initialized, skipping");
            return;
        }

        // Resolve the LTAI-Chat agent to trigger AgentBuilder → populates AgentToolStore
        var toolStore = _sp.GetService<AgentToolStore>();
        var tools = toolStore?.GetTools("LTAI-Chat");
        if (tools == null || tools.Count == 0)
        {
            _logger.LogDebug("WarmupService: resolving LTAI-Chat to populate tools...");
            var chatAgent = _sp.GetKeyedService<AIAgent>("LTAI-Chat");
            _logger.LogDebug("WarmupService: LTAI-Chat resolved ({Name})", chatAgent?.Name ?? "null");
            tools = toolStore?.GetTools("LTAI-Chat");
        }

        if (tools != null && tools.Count > 0)
        {
            _logger.LogInformation("WarmupService: initializing ToolRegistry with {Count} tools...", tools.Count);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await ToolRegistry.InitializeAsync(tools, _embedder, _toolCache, ct).ConfigureAwait(false);
            sw.Stop();
            _logger.LogInformation("WarmupService: ToolRegistry initialized in {Ms}ms", sw.ElapsedMilliseconds);
        }
        else
        {
            _logger.LogWarning("WarmupService: no tools available for ToolRegistry warmup");
        }
    }

    private async Task WarmupKbGraphAsync(CancellationToken ct)
    {
        _logger.LogInformation("WarmupService: computing KbGraph centroids...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        KbGraph.IsKnowledgeQuery("warmup probe query for centroid computation");
        sw.Stop();
        _logger.LogInformation("WarmupService: KbGraph centroids computed in {Ms}ms", sw.ElapsedMilliseconds);
    }

    private async Task WarmupPalaceStoreAsync(CancellationToken ct)
    {
        if (_palaceStore == null) return;
        _logger.LogInformation("WarmupService: warming PalaceStore HNSW index...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _palaceStore.WarmupHnswAsync().ConfigureAwait(false);
        sw.Stop();
        _logger.LogInformation("WarmupService: PalaceStore HNSW warmed in {Ms}ms", sw.ElapsedMilliseconds);
    }
}
