using LiteDB;

namespace LTAI.Agent.Vector;

/// <summary>Simple LiteDB-backed vector store for semantic search.</summary>
public sealed class LiteDbVectorStore : IDisposable
{
    private readonly LiteDatabase _db;

    public LiteDbVectorStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new LiteDatabase(dbPath);
    }

    public void Upsert(string collection, string id, float[] vector, Dictionary<string, object?>? metadata = null)
    {
        var col = _db.GetCollection(collection);
        var doc = new BsonDocument { ["_id"] = id, ["v"] = new BsonArray(vector.Select(f => new BsonValue((double)f))) };
        if (metadata != null)
            foreach (var (k, v) in metadata) doc[k] = v switch { string s => s, int i => i, double d => d, float f => (double)f, bool b => b, null => BsonValue.Null, _ => v.ToString() };
        col.Upsert(doc);
    }

    public List<(string id, float score)> Search(string collection, float[] query, int topN = 5)
    {
        var results = _db.GetCollection(collection).FindAll().Select(doc =>
        {
            var vec = doc["v"].AsArray.Select(x => (float)x.AsDouble).ToArray();
            return (id: doc["_id"].AsString, score: CosineSimilarity(query, vec));
        }).OrderByDescending(r => r.score).Take(topN).ToList();
        return results;
    }

    public void Delete(string collection, string id) => _db.GetCollection(collection).Delete(id);
    public void Dispose() => _db.Dispose();

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na > 0 && nb > 0 ? dot / (MathF.Sqrt(na) * MathF.Sqrt(nb)) : 0;
    }
}
