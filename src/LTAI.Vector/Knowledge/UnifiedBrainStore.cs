using System.Data;
using LTAI.Core.System;
using LTAI.Vector.Knowledge.Models;
using LTAI.Vector.Interfaces;
using LTAI.Vector.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LTAI.Vector.Knowledge;

public sealed class UnifiedBrainStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly IVectorStore _vectorStore;
    private readonly Bm25Scorer _bm25;
    private readonly CompiledTruthStore _truthStore;
    private readonly ILogger<UnifiedBrainStore> _logger;
    private const double RrfK = 60.0;
    private const int ChunkSize = 1000;
    private const int ChunkOverlap = 200;

    public UnifiedBrainStore(
        DataPathResolver dataPath,
        IVectorStore vectorStore,
        ILogger<UnifiedBrainStore> logger)
    {
        var dbPath = dataPath.GetPath("brain.db");
        _vectorStore = vectorStore;
        _logger = logger;

        var dir = global::System.IO.Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !global::System.IO.Directory.Exists(dir))
            global::System.IO.Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        CreateAllTables();

        _bm25 = new Bm25Scorer();
        _truthStore = new CompiledTruthStore(dataPath);

        LoadBm25Index();
    }

    private void CreateAllTables()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS documents (
                id TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL,
                content TEXT NOT NULL,
                domain TEXT DEFAULT 'general',
                category TEXT DEFAULT 'document',
                source TEXT DEFAULT 'manual',
                author TEXT DEFAULT 'system',
                revision INTEGER DEFAULT 1,
                importance REAL DEFAULT 0.0,
                parent_id TEXT,
                section_path TEXT DEFAULT '',
                valid_from TEXT,
                valid_to TEXT,
                created_at REAL DEFAULT (unixepoch()),
                updated_at REAL DEFAULT (unixepoch()),
                metadata TEXT DEFAULT '{}',
                doc_id TEXT,
                chunk_index INTEGER,
                start_char INTEGER
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS docs_fts USING fts5 (
                title, content, domain, category, section_path
            );

            CREATE TABLE IF NOT EXISTS relations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_id TEXT NOT NULL,
                target_id TEXT NOT NULL,
                relation TEXT DEFAULT 'references',
                weight REAL DEFAULT 1.0,
                properties TEXT DEFAULT '{}',
                created_at REAL DEFAULT (unixepoch()),
                UNIQUE(source_id, target_id, relation)
            );

            CREATE TABLE IF NOT EXISTS knowledge_entities (
                id TEXT NOT NULL UNIQUE,
                label TEXT NOT NULL,
                entity_type TEXT DEFAULT 'concept',
                properties TEXT DEFAULT '{}',
                created_at REAL DEFAULT (unixepoch()),
                updated_at REAL DEFAULT (unixepoch())
            );

            CREATE TABLE IF NOT EXISTS knowledge_triplets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                subject TEXT NOT NULL,
                predicate TEXT NOT NULL,
                object TEXT NOT NULL,
                source_text TEXT DEFAULT '',
                confidence REAL DEFAULT 1.0,
                created_at REAL DEFAULT (unixepoch())
            );

            CREATE TABLE IF NOT EXISTS memory_events (
                id TEXT NOT NULL UNIQUE,
                session_id TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                fact_perspective TEXT DEFAULT '',
                rel_perspective TEXT DEFAULT '',
                embedding TEXT DEFAULT '',
                sources TEXT DEFAULT '',
                persona_domain TEXT DEFAULT '',
                emotional_valence REAL DEFAULT 0.0,
                created_at REAL DEFAULT (unixepoch())
            );

            CREATE TABLE IF NOT EXISTS memory_synthesis (
                id TEXT NOT NULL UNIQUE,
                timestamp TEXT NOT NULL,
                content TEXT NOT NULL,
                source_entries TEXT DEFAULT '[]',
                session_ids TEXT DEFAULT '[]',
                model_category TEXT DEFAULT 'general',
                confidence REAL DEFAULT 0.5,
                evidence_count INTEGER DEFAULT 0,
                created_at REAL DEFAULT (unixepoch())
            );

            CREATE TABLE IF NOT EXISTS ingest_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                page_id TEXT,
                source TEXT,
                content_length INTEGER,
                truth_changed INTEGER DEFAULT 0,
                confidence_delta REAL DEFAULT 0,
                ingested_at REAL DEFAULT (unixepoch())
            );

            CREATE INDEX IF NOT EXISTS idx_docs_domain ON documents(domain);
            CREATE INDEX IF NOT EXISTS idx_docs_category ON documents(category);
            CREATE INDEX IF NOT EXISTS idx_docs_parent ON documents(parent_id);
            CREATE INDEX IF NOT EXISTS idx_docs_doc_id ON documents(doc_id);
            CREATE INDEX IF NOT EXISTS idx_rel_source ON relations(source_id);
            CREATE INDEX IF NOT EXISTS idx_rel_target ON relations(target_id);
            CREATE INDEX IF NOT EXISTS idx_rel_type ON relations(relation);
            CREATE INDEX IF NOT EXISTS idx_ke_type ON knowledge_entities(entity_type);
            CREATE INDEX IF NOT EXISTS idx_kt_subject ON knowledge_triplets(subject);
            CREATE INDEX IF NOT EXISTS idx_kt_predicate ON knowledge_triplets(predicate);
            CREATE INDEX IF NOT EXISTS idx_me_session ON memory_events(session_id);
            CREATE INDEX IF NOT EXISTS idx_me_role ON memory_events(role);
            CREATE INDEX IF NOT EXISTS idx_il_page ON ingest_log(page_id);
            CREATE INDEX IF NOT EXISTS idx_il_time ON ingest_log(ingested_at);
            """;
        cmd.ExecuteNonQuery();
    }

    private void LoadBm25Index()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, COALESCE(content, '') as content FROM documents";
        using var reader = cmd.ExecuteReader();
        var docs = new List<(string id, string content)>();
        while (reader.Read())
        {
            docs.Add((reader["id"].ToString() ?? "", reader["content"].ToString() ?? ""));
        }
        if (docs.Count > 0)
            _bm25.IndexDocuments(docs);
    }

    public string AddDocument(string title, string content, string domain = "general",
        string category = "document", string source = "manual", string author = "system",
        double importance = 0.0, string? parentId = null, string? sectionPath = null)
    {
        var chunks = SplitChunks(content);
        var docId = Guid.NewGuid().ToString("N")[..12];
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var (chunk, idx, start) in chunks.Select((c, i) => (c, i, i * (ChunkSize - ChunkOverlap))))
        {
            var id = $"{docId}_c{idx}";
            InsertDocument(id, title, chunk, domain, category, source, author,
                importance, parentId, sectionPath, now, docId, idx, start);
            InsertFtsEntry(id, title, chunk, domain, category, sectionPath ?? "");
            _bm25.AddDocument(id, chunk);
        }

        _vectorStore.AddVectorsAsync(chunks.Select(c => (
            $"v_{Guid.NewGuid():N}",
            (float[])new float[384]
        )).ToList());

        return docId;
    }

    public List<Bm25ScoredDoc> SearchThreeLayer(
        string query,
        string? domain = null,
        int topK = 20,
        int ftsTopK = 30,
        int bm25TopK = 30,
        int vectorTopK = 20)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var ftsResults = SearchFts(query, domain, ftsTopK);
        var bm25Results = _bm25.Search(query, bm25TopK);
        var vectorResults = SearchVector(query, vectorTopK);

        var scoredDocs = RrfFuse(ftsResults, bm25Results, vectorResults, topK);

        _logger?.LogDebug(
            "ThreeLayer search: query='{Query}', fts={Fts}, bm25={Bm25}, vector={Vec}, fused={Fused}, {Ms}ms",
            query[..Math.Min(30, query.Length)], ftsResults.Count, bm25Results.Count,
            vectorResults.Count, scoredDocs.Count, sw.ElapsedMilliseconds);

        return scoredDocs;
    }

    private List<(string id, double score, string content, string source)> SearchFts(
        string query, string? domain, int topK)
    {
        var results = new List<(string, double, string, string)>();
        using var cmd = _conn.CreateCommand();

        if (domain != null)
        {
            cmd.CommandText = """
                SELECT d.id, d.content, d.source, rank
                FROM docs_fts f
                JOIN documents d ON f.rowid = d.rowid
                WHERE docs_fts MATCH $q AND d.domain = $domain
                ORDER BY rank LIMIT $k
                """;
            cmd.Parameters.AddWithValue("$domain", domain);
        }
        else
        {
            cmd.CommandText = """
                SELECT d.id, d.content, d.source, rank
                FROM docs_fts f
                JOIN documents d ON f.rowid = d.rowid
                WHERE docs_fts MATCH $q
                ORDER BY rank LIMIT $k
                """;
        }

        cmd.Parameters.AddWithValue("$q", query);
        cmd.Parameters.AddWithValue("$k", topK);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var rank = reader["rank"] != DBNull.Value ? Convert.ToDouble(reader["rank"]) : 0;
            results.Add((
                reader["id"].ToString() ?? "",
                rank,
                reader["content"].ToString() ?? "",
                reader["source"].ToString() ?? "fts"
            ));
        }

        return results;
    }

    private List<(string id, double score, string content, string source)> SearchVector(
        string query, int topK)
    {
        var results = new List<(string, double, string, string)>();
        try
        {
            var embedding = _vectorStore.EmbedAsync(query).GetAwaiter().GetResult();
            if (embedding.Length == 0) return results;

            var vectorResults = _vectorStore.SearchSimilarAsync(embedding, topK).GetAwaiter().GetResult();
            foreach (var vr in vectorResults)
            {
                results.Add((vr.Id, vr.Score, vr.Text ?? "", "vector"));
            }
        }
        catch { /* non-fatal */ }
        return results;
    }

    private List<Bm25ScoredDoc> RrfFuse(
        List<(string id, double score, string content, string source)> fts,
        List<(string id, double score)> bm25,
        List<(string id, double score, string content, string source)> vector,
        int topK)
    {
        var ftsRanks = Enumerable.Range(1, fts.Count)
            .ToDictionary(i => fts[i - 1].id, i => 1.0 / (RrfK + i));

        var bm25Ranks = Enumerable.Range(1, bm25.Count)
            .ToDictionary(i => bm25[i - 1].id, i => 1.0 / (RrfK + i));

        var vecRanks = Enumerable.Range(1, vector.Count)
            .ToDictionary(i => vector[i - 1].id, i => 1.0 / (RrfK + i));

        var allIds = new HashSet<string>();
        foreach (var (id, _, _, _) in fts) allIds.Add(id);
        foreach (var (id, _) in bm25) allIds.Add(id);
        foreach (var (id, _, _, _) in vector) allIds.Add(id);

        var fused = new List<Bm25ScoredDoc>();
        foreach (var id in allIds)
        {
            var ftsRank = ftsRanks.GetValueOrDefault(id, 0);
            var bm25Rank = bm25Ranks.GetValueOrDefault(id, 0);
            var vecRank = vecRanks.GetValueOrDefault(id, 0);

            var rrfScore = ftsRank * 0.3 + bm25Rank * 0.35 + vecRank * 0.35;

            var content = fts.FirstOrDefault(f => f.id == id).content
                ?? vector.FirstOrDefault(v => v.id == id).content ?? "";

            var source = fts.FirstOrDefault(f => f.id == id).source
                ?? vector.FirstOrDefault(v => v.id == id).source ?? "unknown";

            fused.Add(new Bm25ScoredDoc
            {
                Id = id,
                Content = content,
                Bm25Score = bm25Ranks.GetValueOrDefault(id, 0),
                FtsScore = ftsRanks.GetValueOrDefault(id, 0),
                VectorScore = vecRanks.GetValueOrDefault(id, 0),
                RrfScore = rrfScore,
                Source = source
            });
        }

        return fused.OrderByDescending(d => d.RrfScore).Take(topK).ToList();
    }

    public void AddEntity(string id, string label, string entityType = "concept",
        Dictionary<string, object>? properties = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO knowledge_entities (id, label, entity_type, properties)
            VALUES ($id, $label, $type, $props)
            ON CONFLICT(id) DO UPDATE SET label = $label, properties = $props, updated_at = unixepoch()
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$label", label);
        cmd.Parameters.AddWithValue("$type", entityType);
        cmd.Parameters.AddWithValue("$props", System.Text.Json.JsonSerializer.Serialize(properties ?? new()));
        cmd.ExecuteNonQuery();
    }

    public void AddTriplet(string subject, string predicate, string obj,
        string sourceText = "", double confidence = 1.0)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO knowledge_triplets (subject, predicate, object, source_text, confidence)
            VALUES ($s, $p, $o, $st, $c)
            """;
        cmd.Parameters.AddWithValue("$s", subject);
        cmd.Parameters.AddWithValue("$p", predicate);
        cmd.Parameters.AddWithValue("$o", obj);
        cmd.Parameters.AddWithValue("$st", sourceText);
        cmd.Parameters.AddWithValue("$c", confidence);
        cmd.ExecuteNonQuery();
    }

    public void AddMemoryEvent(string id, string sessionId, string timestamp,
        string role, string content, string factPerspective = "",
        string relPerspective = "", double emotionalValence = 0.0,
        string personaDomain = "")
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memory_events (id, session_id, timestamp, role, content,
                fact_perspective, rel_perspective, emotional_valence, persona_domain)
            VALUES ($id, $sid, $ts, $role, $content, $fp, $rp, $ev, $pd)
            ON CONFLICT(id) DO UPDATE SET content = $content
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$ts", timestamp);
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$fp", factPerspective);
        cmd.Parameters.AddWithValue("$rp", relPerspective);
        cmd.Parameters.AddWithValue("$ev", emotionalValence);
        cmd.Parameters.AddWithValue("$pd", personaDomain);
        cmd.ExecuteNonQuery();
    }

    public List<EventEntry> GetSessionEvents(string sessionId, int limit = 50)
    {
        var events = new List<EventEntry>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM memory_events WHERE session_id = $sid ORDER BY timestamp LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            events.Add(new EventEntry(
                Id: reader["id"].ToString() ?? "",
                SessionId: reader["session_id"].ToString() ?? "",
                Timestamp: reader["timestamp"].ToString() ?? "",
                Role: reader["role"].ToString() ?? "",
                Content: reader["content"].ToString() ?? "",
                FactPerspective: reader["fact_perspective"].ToString() ?? "",
                RelPerspective: reader["rel_perspective"].ToString() ?? "",
                PersonaDomain: reader["persona_domain"].ToString() ?? "",
                EmotionalValence: Convert.ToDouble(reader["emotional_valence"])
            ));
        }
        return events;
    }

    public IReadOnlyList<(string id, string content)> GetDocumentChunks(string docId)
    {
        var chunks = new List<(string, string)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, content FROM documents WHERE doc_id = $did ORDER BY chunk_index";
        cmd.Parameters.AddWithValue("$did", docId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            chunks.Add((reader["id"].ToString() ?? "", reader["content"].ToString() ?? ""));
        return chunks;
    }

    public Bm25Scorer Bm25 => _bm25;
    public CompiledTruthStore Truth => _truthStore;

    public Dictionary<string, object> GetStats()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM documents) as docs,
                (SELECT COUNT(*) FROM knowledge_entities) as entities,
                (SELECT COUNT(*) FROM knowledge_triplets) as triplets,
                (SELECT COUNT(*) FROM memory_events) as events,
                (SELECT COUNT(*) FROM memory_synthesis) as synthesis
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return new();

        var result = new Dictionary<string, object>
        {
            ["documents"] = Convert.ToInt32(reader["docs"]),
            ["entities"] = Convert.ToInt32(reader["entities"]),
            ["triplets"] = Convert.ToInt32(reader["triplets"]),
            ["events"] = Convert.ToInt32(reader["events"]),
            ["synthesis"] = Convert.ToInt32(reader["synthesis"]),
            ["vector_store"] = _vectorStore.GetStatsAsync().GetAwaiter().GetResult(),
            ["bm25"] = _bm25.GetStats(),
            ["compiled_truth"] = _truthStore.GetStats()
        };

        return result;
    }

    private void InsertDocument(string id, string title, string content, string domain,
        string category, string source, string author, double importance,
        string? parentId, string? sectionPath, double now, string docId, int chunkIdx, int startChar)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO documents (id, title, content, domain, category, source, author,
                importance, parent_id, section_path, created_at, updated_at,
                doc_id, chunk_index, start_char)
            VALUES ($id, $title, $content, $domain, $cat, $src, $author,
                $imp, $pid, $sp, $now, $now, $did, $ci, $sc)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$domain", domain);
        cmd.Parameters.AddWithValue("$cat", category);
        cmd.Parameters.AddWithValue("$src", source);
        cmd.Parameters.AddWithValue("$author", author);
        cmd.Parameters.AddWithValue("$imp", importance);
        cmd.Parameters.AddWithValue("$pid", (object?)parentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sp", (object?)sectionPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$did", docId);
        cmd.Parameters.AddWithValue("$ci", chunkIdx);
        cmd.Parameters.AddWithValue("$sc", startChar);
        cmd.ExecuteNonQuery();
    }

    private void InsertFtsEntry(string id, string title, string content, string domain,
        string category, string sectionPath)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO docs_fts (rowid, title, content, domain, category, section_path)
            SELECT rowid, $title, $content, $domain, $cat, $sp
            FROM documents WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$domain", domain);
        cmd.Parameters.AddWithValue("$cat", category);
        cmd.Parameters.AddWithValue("$sp", sectionPath);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static List<string> SplitChunks(string content)
    {
        var chunks = new List<string>();
        for (int i = 0; i < content.Length; i += ChunkSize - ChunkOverlap)
        {
            var end = Math.Min(i + ChunkSize, content.Length);
            chunks.Add(content[i..end]);
        }
        return chunks;
    }

    public void Dispose()
    {
        _conn?.Dispose();
        _truthStore?.Dispose();
    }
}
