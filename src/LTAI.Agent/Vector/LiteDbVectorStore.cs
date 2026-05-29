using System.Linq.Expressions;
using LiteDB;
using Microsoft.Extensions.VectorData;

namespace LTAI.Agent.Vector;

public sealed class LiteDbVectorStore : VectorStore
{
    private readonly LiteDatabase _db;
    private readonly string _dbPath;

    public LiteDbVectorStore(string dbPath)
    {
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new LiteDatabase(dbPath);
    }

    public override VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(string name, VectorStoreCollectionDefinition? definition = null)
        where TKey : notnull
        => new LiteDbCollection<TKey, TRecord>(_db, name);

    public override IVectorStoreRecordCollection<TKey, TRecord> GetDynamicCollection<TKey, TRecord>(string name, VectorStoreCollectionDefinition? definition = null)
        where TKey : notnull
        => new LiteDbCollection<TKey, TRecord>(_db, name);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _db.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class LiteDbCollection<TKey, TRecord> : VectorStoreCollection<TKey, TRecord>
    where TKey : notnull
{
    private readonly ILiteCollection<BsonDocument> _col;

    public override string Name => _col.Name;

    public LiteDbCollection(LiteDatabase db, string name) => _col = db.GetCollection(name);

    public override Task<bool> CollectionExistsAsync(CancellationToken ct = default)
        => Task.FromResult(_col.Count() > 0);

    public override Task EnsureCollectionExistsAsync(CancellationToken ct = default)
    {
        _col.EnsureIndex("$.**");
        return Task.CompletedTask;
    }

    public override Task EnsureCollectionDeletedAsync(CancellationToken ct = default)
    {
        _col.DeleteAll();
        return Task.CompletedTask;
    }

    public override async Task<TRecord?> GetAsync(TKey key, RecordRetrievalOptions? options = null, CancellationToken ct = default)
    {
        var doc = _col.FindById(new BsonValue(key.ToString()));
        return await Task.FromResult(doc == null ? default : FromBson<TRecord>(doc));
    }

    public override async Task<IReadOnlyList<TRecord?>> GetAsync(IEnumerable<TKey> keys, RecordRetrievalOptions? options = null, CancellationToken ct = default)
    {
        var ids = keys.Select(k => new BsonValue(k.ToString()));
        return await Task.FromResult<IReadOnlyList<TRecord?>>(
            _col.Find(Query.In("_id", ids)).Select(FromBson<TRecord>).ToList());
    }

    public override Task DeleteAsync(TKey key, CancellationToken ct = default)
    {
        _col.Delete(new BsonValue(key.ToString()));
        return Task.CompletedTask;
    }

    public override Task DeleteAsync(IEnumerable<TKey> keys, CancellationToken ct = default)
    {
        foreach (var k in keys) _col.Delete(new BsonValue(k.ToString()));
        return Task.CompletedTask;
    }

    public override async Task<TKey> UpsertAsync(TRecord record, CancellationToken ct = default)
    {
        var doc = ToBson(record);
        var id = doc["_id"]?.AsString ?? Guid.NewGuid().ToString("N");
        doc["_id"] = id;
        _col.Upsert(doc);
        return await Task.FromResult((TKey)(object)id);
    }

    public override async Task<IReadOnlyList<TKey>> UpsertAsync(IEnumerable<TRecord> records, CancellationToken ct = default)
    {
        var ids = new List<TKey>();
        foreach (var r in records) ids.Add(await UpsertAsync(r, ct));
        return ids;
    }

    public override async Task<IReadOnlyList<TRecord?>> GetAsync(Expression<Func<TRecord, bool>> filter, int topN,
        FilteredRecordRetrievalOptions<TRecord>? options = null, CancellationToken ct = default)
    {
        var all = _col.FindAll().Select(FromBson<TRecord>).ToList();
        return await Task.FromResult<IReadOnlyList<TRecord?>>(all.Take(topN).ToList());
    }

    public override async Task<VectorSearchResult<TRecord>?> SearchAsync<TVector>(TVector vector, int topN,
        VectorSearchOptions<TRecord>? options = null, CancellationToken ct = default)
    {
        if (vector is not ReadOnlyMemory<float> vec) return null;
        var docs = _col.FindAll().ToList();
        var scored = new List<(float score, TRecord)>();

        foreach (var doc in docs)
        {
            var record = FromBson<TRecord>(doc);
            var v = ExtractVector(record);
            if (v.HasValue) scored.Add((CosineSimilarity(vec.Span, v.Value.Span), record));
        }

        return new VectorSearchResult<TRecord>(
            scored.OrderByDescending(x => x.score).Take(topN)
                  .Select(x => new VectorSearchResult<TRecord>(x.record, x.score)).ToList());
    }

    public override object? GetService(Type serviceType, object? serviceKey = null) => null;

    private static TRecord FromBson<TRecord>(BsonDocument doc)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(ToDict(doc));
        return System.Text.Json.JsonSerializer.Deserialize<TRecord>(json)!;
    }

    private static Dictionary<string, object?> ToDict(BsonDocument doc)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var k in doc.Keys)
        {
            var v = doc[k];
            dict[k] = v.IsArray ? v.AsArray.Select(x => x.RawValue).ToArray()
                   : v.IsDocument ? ToDict(v.AsDocument)
                   : v.RawValue;
        }
        return dict;
    }

    private static BsonDocument ToBson(TRecord record)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(record);
        var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json)!;
        var doc = new BsonDocument();
        foreach (var (k, v) in dict)
        {
            doc[k] = v.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => new BsonValue(v.GetString()),
                System.Text.Json.JsonValueKind.Number when v.TryGetInt32(out var i) => new BsonValue(i),
                System.Text.Json.JsonValueKind.Number when v.TryGetSingle(out var f) => new BsonValue(f),
                System.Text.Json.JsonValueKind.Number when v.TryGetDouble(out var d) => new BsonValue(d),
                System.Text.Json.JsonValueKind.True => new BsonValue(true),
                System.Text.Json.JsonValueKind.False => new BsonValue(false),
                _ => BsonValue.Null
            };
        }
        return doc;
    }

    private static ReadOnlyMemory<float>? ExtractVector(TRecord record)
    {
        if (record == null) return null;
        var json = System.Text.Json.JsonSerializer.Serialize(record);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Array && prop.Value.GetArrayLength() > 4)
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
        for (int i = 0; i < a.Length; i++) { dot += a[i] * a[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na > 0 && nb > 0 ? dot / (MathF.Sqrt(na) * MathF.Sqrt(nb)) : 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _col?.EnsureCollectionDeletedAsync();
        base.Dispose(disposing);
    }
}
