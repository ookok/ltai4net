using LiteDB;

namespace LTAI.Agent.Vector;

/// <summary>Simple LiteDB-backed vector store for semantic search.</summary>
public sealed class LiteDbVectorStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly int _dimension;

    public LiteDbVectorStore(string dbPath, int dimension = 384)
    {
        _dimension = dimension;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new LiteDatabase(dbPath);
    }

    /// <summary>Store a vector with metadata.</summary>
    public void Upsert(string collection, string id, float[] vector, Dictionary<string, object?>? metadata = null)
    {
        var col = _db.GetCollection(collection);
        var doc = new BsonDocument
        {
            ["_id"] = id,
            ["v"] = new BsonArray(vector.Select(f => new BsonValue((double)f))),
            ["m"] = ToBsonDoc(metadata)
        };
        col.Upsert(doc);
    }

    /// <summary>Search for similar vectors by cosine similarity.</summary>
    public List<(string id, float score, Dictionary<string, object?>? metadata)> Search(string collection, float[] query, int topN = 5)
    {
        var col = _db.GetCollection(collection);
        var results = new List<(string, float, Dictionary<string, object?>?)>();

        foreach (var doc in col.FindAll())
        {
            var vec = doc["v"].AsArray.Select(x => (float)x.AsDouble).ToArray();
            var score = CosineSimilarity(query, vec);
            var meta = FromBsonDoc(doc["m"].AsDocument);
            results.Add((doc["_id"].AsString, score, meta));
        }

        return results.OrderByDescending(x => x.Item2).Take(topN).ToList();
    }

    /// <summary>Delete a vector by ID.</summary>
    public void Delete(string collection, string id)
        => _db.GetCollection(collection).Delete(id);

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na > 0 && nb > 0 ? dot / (MathF.Sqrt(na) * MathF.Sqrt(nb)) : 0;
    }

    private static BsonDocument ToBsonDoc(Dictionary<string, object?>? dict)
    {
        var doc = new BsonDocument();
        if (dict == null) return doc;
        foreach (var (k, v) in dict)
            doc[k] = v switch
            {
                string s => s,
                int i => i,
                double d => d,
                float f => (double)f,
                bool b => b,
                null => BsonValue.Null,
                _ => v.ToString()
            };
        return doc;
    }

    private static Dictionary<string, object?>? FromBsonDoc(BsonDocument doc)
    {
        if (doc == null) return null;
        var dict = new Dictionary<string, object?>();
        foreach (var k in doc.Keys) dict[k] = doc[k].RawValue;
        return dict;
    }

    public void Dispose() => _db.Dispose();
}
