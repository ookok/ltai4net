using System.Text.Json;
using LiteDB;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace LTAI.Agent.VectorStore;

/// <summary>LiteDB-backed vector store for MAF ChatHistoryMemoryProvider and RAG.</summary>
public sealed class LiteDbVectorStore : IVectorStore
{
    private readonly LiteDatabase _db;

    public LiteDbVectorStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new LiteDatabase(dbPath);
    }

    public IVectorStoreRecordCollection<TKey, TRecord> GetCollection<TKey, TRecord>(string name, VectorStoreCollectionOptions? options = null)
        where TKey : notnull
    {
        return (IVectorStoreRecordCollection<TKey, TRecord>)new LiteDbCollection<TRecord>(_db, name);
    }

    public Task<IReadOnlyList<string>> GetCollectionNamesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(_db.GetCollectionNames().ToList());

    public void Dispose() => _db.Dispose();
}

internal sealed class LiteDbCollection<TRecord> : IVectorStoreRecordCollection<string, TRecord>
{
    private readonly ILiteCollection<BsonDocument> _col;

    public LiteDbCollection(LiteDatabase db, string name)
    {
        _col = db.GetCollection(name);
        _col.EnsureIndex("$.**");
    }

    public string CollectionName => _col.Name;

    public async Task<TRecord?> GetAsync(string key, GetRecordOptions? options = null, CancellationToken ct = default)
    {
        var doc = _col.FindById(key);
        if (doc == null) return default;
        return await Task.FromResult(BsonToRecord<TRecord>(doc));
    }

    public async Task<IReadOnlyList<TRecord>> GetBatchAsync(IEnumerable<string> keys, GetRecordOptions? options = null, CancellationToken ct = default)
    {
        var ids = keys.Select(k => new BsonValue(k));
        var docs = _col.Find(Query.In("_id", ids));
        return await Task.FromResult(docs.Select(BsonToRecord<TRecord>).ToList());
    }

    public Task DeleteAsync(string key, DeleteRecordOptions? options = null, CancellationToken ct = default)
    {
        _col.Delete(key);
        return Task.CompletedTask;
    }

    public Task DeleteBatchAsync(IEnumerable<string> keys, DeleteRecordOptions? options = null, CancellationToken ct = default)
    {
        foreach (var k in keys) _col.Delete(k);
        return Task.CompletedTask;
    }

    public async Task<string> UpsertAsync(TRecord record, UpsertRecordOptions? options = null, CancellationToken ct = default)
    {
        var doc = RecordToBson(record);
        var id = doc["_id"]?.AsString ?? Guid.NewGuid().ToString("N");
        doc["_id"] = id;
        _col.Upsert(doc);
        return await Task.FromResult(id);
    }

    public async Task<IReadOnlyList<string>> UpsertBatchAsync(IEnumerable<TRecord> records, UpsertRecordOptions? options = null, CancellationToken ct = default)
    {
        var ids = new List<string>();
        foreach (var r in records)
            ids.Add(await UpsertAsync(r, options, ct));
        return ids;
    }

    public async Task<VectorSearchResult<TRecord>> VectorSearchAsync(TRecord reference, VectorSearchOptions<TRecord>? options = null, CancellationToken ct = default)
    {
        return await Task.FromResult(new VectorSearchResult<TRecord>([]));
    }

    public async Task<VectorSearchResult<TRecord>> VectorSearchAsync(ReadOnlyMemory<float> vector, VectorSearchOptions<TRecord>? options = null, CancellationToken ct = default)
    {
        var docs = _col.FindAll().ToList();
        var scored = new List<(float score, TRecord record)>();

        foreach (var doc in docs)
        {
            var record = BsonToRecord<TRecord>(doc);
            var vec = GetVector(record);
            if (vec.HasValue)
                scored.Add((CosineSimilarity(vector.Span, vec.Value.Span), record));
        }

        var results = scored.OrderByDescending(x => x.score)
            .Take(options?.TopN ?? 5)
            .Select(x => new VectorSearchResult<TRecord>(x.record, x.score))
            .ToList();

        return await Task.FromResult(new VectorSearchResult<TRecord>(results));
    }

    private static TRecord BsonToRecord<TRecord>(BsonDocument doc)
    {
        var json = LiteDB.JsonSerializer.Serialize(doc);
        return System.Text.Json.JsonSerializer.Deserialize<TRecord>(json)!;
    }

    private static BsonDocument RecordToBson(TRecord record)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(record);
        return LiteDB.JsonSerializer.Deserialize(json).AsDocument;
    }

    private static ReadOnlyMemory<float>? GetVector(TRecord record)
    {
        if (record == null) return null;
        var json = System.Text.Json.JsonSerializer.Serialize(record);
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array && prop.Value.GetArrayLength() > 4)
            {
                var arr = prop.Value.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                return arr.AsMemory();
            }
        }
        return null;
    }

    private static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) return 0;
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na > 0 && nb > 0 ? dot / (MathF.Sqrt(na) * MathF.Sqrt(nb)) : 0;
    }

    public void Dispose() { }
}
