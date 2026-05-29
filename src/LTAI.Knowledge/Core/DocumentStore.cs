using System.Text.Json;
using LTAI.Core.System;
using LTAI.Knowledge.Vector.Interfaces;
using LTAI.Knowledge.Core.Models;
using LTAI.Knowledge.Vector.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LTAI.Knowledge.Core;

public sealed class DocumentStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<DocumentStore> _logger;
    private const int ChunkSize = 1000;
    private const int ChunkOverlap = 200;
    private const double RrfK = 60.0;

    public DocumentStore(
        DataPathResolver dataPath,
        IVectorStore vectorStore,
        ILogger<DocumentStore> logger)
    {
        var dbPath = dataPath.GetPath("document_store.db");
        _vectorStore = vectorStore;
        _logger = logger;

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        CreateTables();
    }

    private void CreateTables()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA cache_size=-8000;
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
                title,
                content,
                domain,
                category,
                section_path
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

            CREATE INDEX IF NOT EXISTS idx_docs_domain ON documents(domain);
            CREATE INDEX IF NOT EXISTS idx_docs_category ON documents(category);
            CREATE INDEX IF NOT EXISTS idx_docs_parent ON documents(parent_id);
            CREATE INDEX IF NOT EXISTS idx_docs_doc_id ON documents(doc_id);
            CREATE INDEX IF NOT EXISTS idx_rel_source ON relations(source_id);
            CREATE INDEX IF NOT EXISTS idx_rel_target ON relations(target_id);
            CREATE INDEX IF NOT EXISTS idx_rel_type ON relations(relation);
            """;
        cmd.ExecuteNonQuery();
    }

    public string AddDocument(
        string title,
        string content,
        string domain = "general",
        string category = "document",
        string source = "manual",
        string author = "system",
        double importance = 0.0,
        string? parentId = null,
        string sectionPath = "",
        bool autoChunk = true)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO documents (id, title, content, domain, category, source, author,
                importance, parent_id, section_path, created_at, updated_at)
            VALUES (@id, @title, @content, @domain, @category, @source, @author,
                @importance, @parentId, @sectionPath, @now, @now)
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@domain", domain);
        cmd.Parameters.AddWithValue("@category", category);
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@author", author);
        cmd.Parameters.AddWithValue("@importance", importance);
        cmd.Parameters.AddWithValue("@parentId", (object?)parentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sectionPath", sectionPath);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();

        InsertFts(id, title, content, domain, category, sectionPath);

        if (autoChunk)
        {
            var chunks = SplitChunks(content);
            AddChunks(id, chunks);
        }

        _logger.LogDebug("Added document: {Id} ({Title}), chunks: {Chunks}", id, title, autoChunk ? SplitChunks(content).Count : 0);
        return id;
    }

    public void AddChunks(string docId, List<ChunkInfo> chunks)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var tx = _conn.BeginTransaction();

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunkId = $"{docId}_c{i}";
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO documents (id, title, content, domain, category, doc_id,
                    chunk_index, start_char, created_at, updated_at)
                SELECT @id, title, @content, domain, 'chunk', @docId, @idx, @start, @now, @now
                FROM documents WHERE id = @docId
                """;
            cmd.Parameters.AddWithValue("@id", chunkId);
            cmd.Parameters.AddWithValue("@content", chunks[i].Text);
            cmd.Parameters.AddWithValue("@docId", docId);
            cmd.Parameters.AddWithValue("@idx", i);
            cmd.Parameters.AddWithValue("@start", chunks[i].StartChar);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.ExecuteNonQuery();

            InsertFts(chunkId, "", chunks[i].Text, "", "chunk", "");
        }

        tx.Commit();
    }

    public async Task IndexDocumentVectorAsync(string docId)
    {
        var doc = GetDocument(docId);
        if (doc == null) return;

        var embed = await _vectorStore.EmbedAsync(doc.Content).ConfigureAwait(false);
        await _vectorStore.AddVectorsAsync(new[] { (docId, embed) }).ConfigureAwait(false);
    }

    public DocumentEntity? GetDocument(string docId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM documents WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", docId);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapDocument(reader) : null;
    }

    public List<DocumentEntity> GetChunks(string docId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM documents WHERE doc_id = @id ORDER BY chunk_index";
        cmd.Parameters.AddWithValue("@id", docId);

        var chunks = new List<DocumentEntity>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            chunks.Add(MapDocument(reader));
        return chunks;
    }

    public async Task<List<KnowledgeSearchResult>> Search(string query, string? domain = null, int topK = 10)
    {
        var ftsResults = SearchFts(query, domain, topK * 2);
        var vectorResults = await SearchVectorAsync(query, topK * 2).ConfigureAwait(false);
        return RrfMerge(ftsResults, vectorResults).Take(topK).ToList();
    }

    public async Task<List<KnowledgeSearchResult>> SearchWithRerank(string query, string? domain = null, int topK = 10)
    {
        var ftsResults = SearchFts(query, domain, topK * 3);
        var vectorResults = await SearchVectorAsync(query, topK * 3).ConfigureAwait(false);
        var merged = RrfMerge(ftsResults, vectorResults).Take(topK * 2).ToList();

        var scoredDocs = merged.Select(r => new Bm25ScoredDoc
        {
            Id = r.Id, Content = r.Content ?? "",
            Bm25Score = r.Score, VectorScore = r.Score, RrfScore = r.Score, Source = r.Source
        }).ToList();

        var reranked = scoredDocs.OrderByDescending(d => d.VectorScore + d.Bm25Score).Take(topK).ToList();

        var result = new List<KnowledgeSearchResult>();
        foreach (var doc in reranked)
        {
            var orig = merged.FirstOrDefault(m => m.Id == doc.Id);
            result.Add(new KnowledgeSearchResult
            {
                Id = doc.Id,
                Title = orig?.Title ?? "",
                Content = doc.Content,
                Domain = orig?.Domain ?? "",
                Score = doc.RrfScore,
                Source = "reranked",
                ChunkIndex = orig?.ChunkIndex ?? 0
            });
        }
        return result;
    }

    public List<KnowledgeSearchResult> SearchFts(string query, string? domain = null, int limit = 20)
    {
        var results = new List<KnowledgeSearchResult>();
        using var cmd = _conn.CreateCommand();

        cmd.CommandText = domain != null
            ? """
              SELECT d.* FROM documents d
              JOIN docs_fts f ON d.rowid = f.rowid
              WHERE docs_fts MATCH @query AND d.domain = @domain
              ORDER BY rank LIMIT @limit
              """
            : """
              SELECT d.* FROM documents d
              JOIN docs_fts f ON d.rowid = f.rowid
              WHERE docs_fts MATCH @query ORDER BY rank LIMIT @limit
              """;

        cmd.Parameters.AddWithValue("@query", query);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (domain != null)
            cmd.Parameters.AddWithValue("@domain", domain);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var doc = MapDocument(reader);
            results.Add(new KnowledgeSearchResult
            {
                Id = doc.Id,
                Title = doc.Title,
                Content = doc.Content,
                Domain = doc.Domain,
                Score = 1.0,
                Source = "fts",
                ChunkIndex = doc.ChunkIndex
            });
        }
        return results;
    }

    public async Task<List<KnowledgeSearchResult>> SearchVectorAsync(string query, int topK = 10)
    {
        try
        {
            var queryVec = await _vectorStore.EmbedAsync(query).ConfigureAwait(false);
            var vectorResults = await _vectorStore.SearchSimilarAsync(queryVec, topK).ConfigureAwait(false);

            return vectorResults.Select(r =>
            {
                var doc = GetDocument(r.Id);
                return new KnowledgeSearchResult
                {
                    Id = r.Id,
                    Title = doc?.Title ?? "",
                    Content = doc?.Content ?? r.Text ?? "",
                    Domain = doc?.Domain ?? "",
                    Score = r.Score,
                    Source = "vector",
                    ChunkIndex = doc?.ChunkIndex
                };
            }).ToList();
        }
        catch
        {
            return new List<KnowledgeSearchResult>();
        }
    }

    public int AddRelation(string sourceId, string targetId, string relation = "references",
        double weight = 1.0, string? properties = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO relations (source_id, target_id, relation, weight, properties, created_at)
            VALUES (@src, @tgt, @rel, @w, @prop, @now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@src", sourceId);
        cmd.Parameters.AddWithValue("@tgt", targetId);
        cmd.Parameters.AddWithValue("@rel", relation);
        cmd.Parameters.AddWithValue("@w", weight);
        cmd.Parameters.AddWithValue("@prop", properties ?? "{}");
        cmd.Parameters.AddWithValue("@now", now);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public List<DocumentEntity> ListDocuments(string? domain = null, string? category = null)
    {
        var docs = new List<DocumentEntity>();
        using var cmd = _conn.CreateCommand();

        var where = new List<string>();
        if (domain != null) { where.Add("domain = @domain"); cmd.Parameters.AddWithValue("@domain", domain); }
        if (category != null) { where.Add("category = @category"); cmd.Parameters.AddWithValue("@category", category); }
        where.Add("chunk_index IS NULL");

        cmd.CommandText = $"SELECT * FROM documents WHERE {string.Join(" AND ", where)} ORDER BY created_at DESC LIMIT 100";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            docs.Add(MapDocument(reader));
        return docs;
    }

    public void DeleteDocument(string docId)
    {
        using var tx = _conn.BeginTransaction();

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM docs_fts WHERE rowid IN (SELECT rowid FROM documents WHERE id = @id OR doc_id = @id)";
            cmd.Parameters.AddWithValue("@id", docId);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM documents WHERE id = @id OR doc_id = @id";
            cmd.Parameters.AddWithValue("@id", docId);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM relations WHERE source_id = @id OR target_id = @id";
            cmd.Parameters.AddWithValue("@id", docId);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public async Task<DocumentStoreStats> GetStats()
    {
        var stats = new DocumentStoreStats();

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM documents WHERE chunk_index IS NULL";
            stats.TotalDocuments = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM documents WHERE chunk_index IS NOT NULL";
            stats.TotalChunks = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM relations";
            stats.TotalRelations = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }

        var vs = await _vectorStore.GetStatsAsync().ConfigureAwait(false);
        stats.TotalVectors = vs.TotalVectors;

        var dbPath = _conn.DataSource;
        if (File.Exists(dbPath))
            stats.DatabaseSizeBytes = new FileInfo(dbPath).Length;

        return stats;
    }

    private static List<KnowledgeSearchResult> RrfMerge(
        List<KnowledgeSearchResult> listA,
        List<KnowledgeSearchResult> listB)
    {
        var scores = new Dictionary<string, double>();

        for (var i = 0; i < listA.Count; i++)
        {
            var id = listA[i].Id;
            scores[id] = (scores.TryGetValue(id, out var s) ? s : 0) + 1.0 / (RrfK + i + 1);
        }
        for (var i = 0; i < listB.Count; i++)
        {
            var id = listB[i].Id;
            scores[id] = (scores.TryGetValue(id, out var s) ? s : 0) + 1.0 / (RrfK + i + 1);
        }

        var merged = new List<KnowledgeSearchResult>();
        var seen = new HashSet<string>();
        foreach (var (id, score) in scores.OrderByDescending(kv => kv.Value))
        {
            var result = listA.Find(r => r.Id == id) ?? listB.Find(r => r.Id == id);
            if (result != null && seen.Add(id))
            {
                merged.Add(result with { Score = score });
            }
        }

        return merged;
    }

    private void InsertFts(string docId, string title, string content, string domain, string category, string sectionPath)
    {
        try
        {
            using var del = _conn.CreateCommand();
            del.CommandText = "DELETE FROM docs_fts WHERE rowid = (SELECT rowid FROM documents WHERE id = @id)";
            del.Parameters.AddWithValue("@id", docId);
            del.ExecuteNonQuery();

            using var ins = _conn.CreateCommand();
            ins.CommandText = """
                INSERT INTO docs_fts (rowid, title, content, domain, category, section_path)
                SELECT rowid, @title, @content, @domain, @category, @sectionPath
                FROM documents WHERE id = @id
                """;
            ins.Parameters.AddWithValue("@id", docId);
            ins.Parameters.AddWithValue("@title", title);
            ins.Parameters.AddWithValue("@content", content);
            ins.Parameters.AddWithValue("@domain", domain);
            ins.Parameters.AddWithValue("@category", category);
            ins.Parameters.AddWithValue("@sectionPath", sectionPath);
            ins.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FTS insert failed for document {Id}", docId);
        }
    }

    public static List<ChunkInfo> SplitChunks(string text)
    {
        var chunks = new List<ChunkInfo>();
        if (string.IsNullOrWhiteSpace(text))
            return chunks;

        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(start + ChunkSize, text.Length);

            if (end < text.Length)
            {
                var splitPoint = text.LastIndexOfAny(new[] { '.', '。', '\n', ' ', '!', '?', '！', '？' }, end);
                if (splitPoint > start + ChunkSize / 2)
                    end = splitPoint + 1;
            }

            chunks.Add(new ChunkInfo
            {
                Text = text[start..end].Trim(),
                StartChar = start
            });

            start = end - ChunkOverlap;
            if (start < 0) start = 0;
        }

        return chunks;
    }

    private static DocumentEntity MapDocument(SqliteDataReader reader)
    {
        return new DocumentEntity
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Title = GetStringSafe(reader, "title"),
            Content = GetStringSafe(reader, "content"),
            Domain = GetStringSafe(reader, "domain"),
            Category = GetStringSafe(reader, "category"),
            Source = GetStringSafe(reader, "source"),
            Author = GetStringSafe(reader, "author"),
            Revision = GetIntSafe(reader, "revision"),
            Importance = GetDoubleSafe(reader, "importance"),
            ParentId = GetNullableString(reader, "parent_id"),
            SectionPath = GetStringSafe(reader, "section_path"),
            ValidFrom = GetNullableString(reader, "valid_from"),
            ValidTo = GetNullableString(reader, "valid_to"),
            CreatedAt = GetDoubleSafe(reader, "created_at"),
            UpdatedAt = GetDoubleSafe(reader, "updated_at"),
            Metadata = GetStringSafe(reader, "metadata"),
            DocId = GetNullableString(reader, "doc_id"),
            ChunkIndex = reader.IsDBNull(reader.GetOrdinal("chunk_index")) ? null : reader.GetInt32(reader.GetOrdinal("chunk_index")),
            StartChar = reader.IsDBNull(reader.GetOrdinal("start_char")) ? null : reader.GetInt32(reader.GetOrdinal("start_char"))
        };
    }

    private static string GetStringSafe(SqliteDataReader r, string col) =>
        r.IsDBNull(r.GetOrdinal(col)) ? string.Empty : r.GetString(r.GetOrdinal(col));

    private static string? GetNullableString(SqliteDataReader r, string col) =>
        r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetString(r.GetOrdinal(col));

    private static int GetIntSafe(SqliteDataReader r, string col) =>
        r.IsDBNull(r.GetOrdinal(col)) ? 0 : r.GetInt32(r.GetOrdinal(col));

    private static double GetDoubleSafe(SqliteDataReader r, string col) =>
        r.IsDBNull(r.GetOrdinal(col)) ? 0.0 : r.GetDouble(r.GetOrdinal(col));

    public void Dispose()
    {
        _conn.Close();
        _conn.Dispose();
        GC.SuppressFinalize(this);
    }
}
