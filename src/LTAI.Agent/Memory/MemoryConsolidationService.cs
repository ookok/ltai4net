using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

/// <summary>
/// Background service that periodically consolidates, decays, and prunes
/// the PalaceStore — analogous to sleep-dependent memory consolidation.
/// Runs every 30 minutes. When a room has >=3 drawers, uses LLM summarization
/// to merge them into a single consolidated entry.
/// </summary>
public sealed class MemoryConsolidationService : BackgroundService
{
    private readonly PalaceStore _store;
    private readonly IChatClient? _llm;
    private readonly ILogger<MemoryConsolidationService>? _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(
        int.TryParse(Environment.GetEnvironmentVariable("LTAI_MEMORY_CONSOLIDATION_MINUTES"), out var m) ? Math.Max(5, m) : 30);

    public MemoryConsolidationService(PalaceStore store, IChatClient? llm = null,
        ILogger<MemoryConsolidationService>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _llm = llm;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation("MemoryConsolidationService: started, interval={Interval}", Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
                if (stoppingToken.IsCancellationRequested) break;
                await ConsolidateOnceAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "MemoryConsolidationService: cycle failed");
            }
        }
    }

    private async Task ConsolidateOnceAsync()
    {
        var expired = _store.CleanupExpired();
        var decayed = _store.DecayAll(factor: 0.95, minImportance: 0.05);
        var rooms = _store.ListRooms();
        var merged = 0;
        var summarizedCount = 0;

        foreach (var wing in _store.ListWings())
        {
            foreach (var (_, roomName) in _store.ListRooms(wing))
            {
                var drawers = _store.GetRecentDrawers(wing, roomName, limit: 20);
                if (drawers.Count <= 2)
                {
                    _store.ConsolidateRoom(wing, roomName);
                    continue;
                }

                // LLM summarization for rooms with >=3 drawers
                if (_llm != null && drawers.Count >= 3)
                {
                    try
                    {
                        var summarized = await SummarizeRoomAsync(wing, roomName, drawers).ConfigureAwait(false);
                        if (summarized > 0)
                        {
                            summarizedCount += summarized;
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "MemoryConsolidationService: LLM summarization failed for {Wing}/{Room}, fallback to plain merge", wing, roomName);
                    }
                }

                _store.ConsolidateRoom(wing, roomName);
            }
        }

        if (expired > 0 || decayed > 0 || merged > 0 || summarizedCount > 0)
            _logger?.LogInformation(
                "MemoryConsolidationService: expired={Expired} decayed={Decayed} merged={Merged} summarized={Summarized} rooms={Rooms}",
                expired, decayed, merged, summarizedCount, rooms.Count);
    }

    private async Task<int> SummarizeRoomAsync(string wing, string room, IReadOnlyList<PalaceStore.Drawer> drawers)
    {
        var contents = drawers.Select(d => d.Content).ToList();
        var joined = string.Join("\n---\n", contents);
        if (joined.Length < 50) return 0;

        var prompt = $"""
            Summarize the following memory entries about "{room}" into a single concise paragraph (max 200 chars). 
            Preserve key facts, decisions, and preferences. Remove redundancy.

            {joined}
            """;

        var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
        var response = await _llm!.GetResponseAsync(messages,
            new ChatOptions { Temperature = 0.1f, MaxOutputTokens = 200 },
            CancellationToken.None).ConfigureAwait(false);

        var summary = response.Text?.Trim();
        if (string.IsNullOrWhiteSpace(summary) || summary.Length < 10) return 0;

        // Store consolidated summary
        var highestImportance = drawers.Max(d => d.Importance);
        await _store.StoreAsync(wing, room, summary,
            role: "consolidation",
            importance: Math.Min(highestImportance + 0.05, 0.95),
            agentId: "consolidator",
            ttlMs: PalaceStore.DefaultTtlMs).ConfigureAwait(false);

        // Delete old drawers
        var deleted = 0;
        foreach (var d in drawers)
        {
            if (_store.DeleteDrawer(d.DrawerId)) deleted++;
        }

        _logger?.LogDebug("MemoryConsolidationService: summarized {Count}→1 for {Wing}/{Room}", drawers.Count, wing, room);
        return deleted;
    }
}
