// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  EmbeddedMemoryProvider — Local Mem0-equivalent AIContextProvider
//
//  Stores conversation messages in a local SQLite database with
//  embedding vectors. At every invocation, retrieves the top-K
//  most relevant historical messages and injects them as a
//  "Memories" prefix in the LLM context.
//
//  Used as a fallback when MEM0_API_KEY is not configured, so the
//  agent retains cross-session long-term memory without an external
//  service.
//
//  Schema:
//    memories(id INTEGER PK, role TEXT, content TEXT,
//             embedding BLOB, created_at INTEGER, agent_id TEXT)
// ═══════════════════════════════════════════════════════════════

using System.Text;
using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;

namespace LTAI.Agent.Memory;

[ToolDomain("memory")]
public sealed class EmbeddedMemoryProvider : AIContextProvider
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS memories (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            role       TEXT    NOT NULL,
            content    TEXT    NOT NULL,
            embedding  BLOB    NOT NULL,
            created_at INTEGER NOT NULL,
            agent_id   TEXT    NOT NULL DEFAULT 'default'
        );
        CREATE INDEX IF NOT EXISTS idx_memories_agent ON memories(agent_id, created_at DESC);
        """;

    private readonly EmbeddingClient _embedder;
    private readonly string _dbPath;
    private readonly int _topK;
    private readonly double _minSimilarity;
    private readonly ILogger<EmbeddedMemoryProvider>? _logger;
    private readonly string _connectionString;
    private bool _schemaReady;
    private readonly object _gate = new();

    public EmbeddedMemoryProvider(
        EmbeddingClient embedder,
        string dbPath,
        int topK = 5,
        double minSimilarity = 0.55,
        ILogger<EmbeddedMemoryProvider>? logger = null)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _dbPath = dbPath;
        _topK = topK;
        _minSimilarity = minSimilarity;
        _logger = logger;

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => ["EmbeddedMemoryProvider"];

    // ─── ProvideAIContextAsync: top-K similarity search ───
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        EnsureSchema();

        var queryText = string.Join('\n', context.AIContext.Messages
            .Where(m => !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => m.Text));

        if (string.IsNullOrWhiteSpace(queryText)) return new AIContext();

        try
        {
            var queryVec = await _embedder.GenerateAsync(queryText, ct).ConfigureAwait(false);
            var hits = SearchAsync(queryVec, _topK, ct);

            var list = new List<string>();
            await foreach (var (content, sim) in hits.WithCancellation(ct).ConfigureAwait(false))
            {
                if (sim < _minSimilarity) break;
                list.Add($"[{sim:F2}] {content}");
            }

            if (list.Count == 0) return new AIContext();

            var prompt = "## Memories (cross-session)\nConsider these earlier conversation fragments when answering:\n"
                + string.Join("\n", list);

            return new AIContext
            {
                Messages = [new ChatMessage(ChatRole.User, prompt)],
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "EmbeddedMemoryProvider: retrieval failed, continuing without memories");
            return new AIContext();
        }
    }

    // ─── StoreAIContextAsync: persist request + response messages ───
    protected override async ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken ct = default)
    {
        EnsureSchema();

        var allMessages = context.RequestMessages
            .Concat(context.ResponseMessages ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m.Text))
            .ToList();

        if (allMessages.Count == 0) return;

        try
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO memories(role, content, embedding, created_at, agent_id)
                VALUES($role, $content, $embedding, $ts, $agent);
                """;

            var pRole = cmd.CreateParameter(); pRole.ParameterName = "$role"; cmd.Parameters.Add(pRole);
            var pContent = cmd.CreateParameter(); pContent.ParameterName = "$content"; cmd.Parameters.Add(pContent);
            var pVec = cmd.CreateParameter(); pVec.ParameterName = "$embedding"; cmd.Parameters.Add(pVec);
            var pTs = cmd.CreateParameter(); pTs.ParameterName = "$ts"; cmd.Parameters.Add(pTs);
            var pAgent = cmd.CreateParameter(); pAgent.ParameterName = "$agent"; cmd.Parameters.Add(pAgent);
            pAgent.Value = "default";

            foreach (var msg in allMessages)
            {
                if (msg.Text.Length > 4000) continue;
                var vec = await _embedder.GenerateAsync(msg.Text, ct).ConfigureAwait(false);

                pRole.Value = msg.Role.Value;
                pContent.Value = msg.Text;
                pVec.Value = SerializeVector(vec);
                pTs.Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            _logger?.LogDebug("EmbeddedMemoryProvider: stored {Count} messages", allMessages.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "EmbeddedMemoryProvider: persist failed");
        }
    }

    // ─── Search: cosine similarity top-K ───
    private async IAsyncEnumerable<(string Content, double Similarity)> SearchAsync(
        float[] queryVec, int k,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT content, embedding FROM memories
            ORDER BY created_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", k * 8);

        var results = new List<(string Content, double Sim)>(k * 8);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var content = reader.GetString(0);
            var blob = (byte[])reader.GetValue(1);
            var vec = DeserializeVector(blob);
            var sim = CosineSimilarity(queryVec, vec);
            results.Add((content, sim));
        }

        foreach (var hit in results.OrderByDescending(r => r.Sim).Take(k))
            yield return hit;
    }

    // ─── Helpers ───
    private void EnsureSchema()
    {
        if (_schemaReady) return;
        lock (_gate)
        {
            if (_schemaReady) return;
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();
            _schemaReady = true;
        }
    }

    private static byte[] SerializeVector(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] DeserializeVector(byte[] bytes)
    {
        var v = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
        return v;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom == 0 ? 0 : dot / denom;
    }
}
