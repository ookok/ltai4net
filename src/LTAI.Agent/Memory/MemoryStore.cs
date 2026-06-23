using System.Collections.Concurrent;
using LTAI.AI;
using LTAI.Core.Memory;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

/// <summary>
/// Unified 3-layer memory store wrapping PalaceStore.
/// L0: Short-term sliding window (in-memory)
/// L1: Long-term structured memory (FTS5 + vector via PalaceStore)
/// L2: Synthesized reflection memory (PalaceStore "reflection" room)
/// </summary>
public sealed class MemoryStore : IMemoryStore, IDisposable
{
    private readonly PalaceStore _palace;
    private readonly EmbeddingClient _embedder;
    private readonly ILogger<MemoryStore> _logger;
    private readonly ConcurrentDictionary<string, LinkedList<ChatMessage>> _shortTerm = new();

    private const int DefaultSlidingWindow = 20;

    public MemoryStore(PalaceStore palace, EmbeddingClient embedder, ILogger<MemoryStore>? logger = null)
    {
        _palace = palace ?? throw new ArgumentNullException(nameof(palace));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MemoryStore>.Instance;
    }

    public PalaceStore Inner => _palace;

    // ══════════════════════════════════════════════
    // L0: Short-term
    // ══════════════════════════════════════════════

    public Task StoreMessageAsync(string traceId, ChatMessage message, CancellationToken ct = default)
    {
        var window = _shortTerm.GetOrAdd(traceId, _ => new LinkedList<ChatMessage>());
        lock (window)
        {
            window.AddLast(message);
            while (window.Count > DefaultSlidingWindow)
                window.RemoveFirst();
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatMessage>> GetRecentMessagesAsync(
        string traceId, int count = 20, CancellationToken ct = default)
    {
        if (_shortTerm.TryGetValue(traceId, out var window))
        {
            lock (window)
            {
                var result = window.TakeLast(Math.Min(count, window.Count)).ToList();
                return Task.FromResult<IReadOnlyList<ChatMessage>>(result);
            }
        }
        return Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());
    }

    // ══════════════════════════════════════════════
    // L1: Long-term
    // ══════════════════════════════════════════════

    public async Task StoreFactAsync(MemoryFact fact, CancellationToken ct = default)
    {
        var meta = new Dictionary<string, object>
        {
            ["fact_id"] = fact.Id,
            ["entities"] = string.Join(",", fact.Entities),
        };
        await _palace.StoreAsync(
            wing: fact.Room,
            room: "default",
            content: fact.Content,
            role: "assistant",
            importance: fact.Importance,
            metadata: meta,
            principal: fact.Principal,
            scope: fact.Scope ?? "shared");
    }

    public async Task<IReadOnlyList<MemoryFact>> SearchFactsAsync(
        string query, int topK = 10, MemoryFilter? filter = null, CancellationToken ct = default)
    {
        var queryVec = await _embedder.GenerateAsync(query, ct);
        if (queryVec == null || queryVec.Length == 0)
            return Array.Empty<MemoryFact>();

        var room = filter?.Room;
        var results = await _palace.HybridSearchAsync(
            queryVec, query, topK, filter?.Room, room,
            principal: filter?.Principal, scope: filter?.Scope);

        return results.Select(d => new MemoryFact(
            Id: d.Drawer.DrawerId,
            Content: d.Drawer.Content,
            Room: d.Drawer.Room,
            Importance: (float)d.Drawer.Importance,
            Entities: Array.Empty<string>(),
            Embedding: d.Drawer.Embedding,
            CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(d.Drawer.CreatedAt).DateTime,
            Principal: d.Drawer.Principal,
            Scope: d.Drawer.Scope
        )).ToList();
    }

    // ══════════════════════════════════════════════
    // L2: Synthesis
    // ══════════════════════════════════════════════

    public async Task<SynthesizedMemory?> SynthesizeAsync(string topic, CancellationToken ct = default)
    {
        var queryVec = await _embedder.GenerateAsync(topic, ct);
        if (queryVec == null || queryVec.Length == 0) return null;

        var results = await _palace.HybridSearchAsync(
            queryVec, topic, 5, wing: "reflection");
        if (results.Count == 0) return null;

        var best = results[0];
        return new SynthesizedMemory(
            Topic: topic,
            Summary: best.Drawer.Content,
            SourceFactIds: Array.Empty<string>(),
            Entities: Array.Empty<string>(),
            CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(best.Drawer.CreatedAt).DateTime
        );
    }

    public async Task<IReadOnlyList<SynthesizedMemory>> GetAllSynthesesAsync(CancellationToken ct = default)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_palace.ConnectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT drawer_id, content, created_at FROM palace WHERE room='reflection' ORDER BY created_at DESC LIMIT 100";
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        var results = new List<SynthesizedMemory>();
        while (rdr.Read())
        {
            results.Add(new SynthesizedMemory(
                Topic: rdr.GetString(0),
                Summary: rdr.GetString(1),
                SourceFactIds: Array.Empty<string>(),
                Entities: Array.Empty<string>(),
                CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(rdr.GetInt64(2)).DateTime
            ));
        }
        return results;
    }

    // ══════════════════════════════════════════════
    // Memorial governance (GateMem-inspired)
    // ══════════════════════════════════════════════

    public async Task ForgetAsync(MemoryForgetRequest request, CancellationToken ct = default)
    {
        await _palace.ForgetAsync(
            drawerId: request.FactId,
            room: request.Room,
            principal: request.Principal,
            scope: request.Scope,
            forgetAll: request.ForgetAll);
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        return await _palace.PurgeExpiredAsync();
    }

    // ══════════════════════════════════════════════
    // Maintenance
    // ══════════════════════════════════════════════

    public Task TrimAsync(int maxEntries = 10000, CancellationToken ct = default)
    {
        _palace.MaxEntries = maxEntries;
        return Task.CompletedTask;
    }

    public void Dispose() => _shortTerm.Clear();
}
