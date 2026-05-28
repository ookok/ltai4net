using LTAI.Knowledge.Core.Models;

namespace LTAI.Knowledge.Core;

/// <summary>
/// Wraps multiple IKnowledgeStore instances. Writes broadcast to all backends.
/// Reads query the first store that returns results.
/// Used during the 5→2 memory consolidation migration.
/// </summary>
public sealed class CompositeKnowledgeStore : IKnowledgeStore
{
    private readonly IReadOnlyList<IKnowledgeStore> _stores;

    public CompositeKnowledgeStore(params IKnowledgeStore[] stores)
    {
        _stores = stores ?? Array.Empty<IKnowledgeStore>();
    }

    public CompositeKnowledgeStore(IEnumerable<IKnowledgeStore> stores)
    {
        _stores = stores?.ToList().AsReadOnly() ?? Array.Empty<IKnowledgeStore>().ToList().AsReadOnly();
    }

    public string AddKnowledge(string title, string content, string domain = "general",
        string category = "document", string source = "manual", string author = "system",
        double importance = 0.0, bool skipDedup = false, bool indexVector = true)
    {
        string? firstId = null;
        foreach (var store in _stores)
        {
            var id = store.AddKnowledge(title, content, domain, category, source, author,
                importance, skipDedup, indexVector);
            firstId ??= id;
        }
        return firstId ?? "";
    }

    public DocumentEntity? Retrieve(string id)
    {
        foreach (var store in _stores)
        {
            var result = store.Retrieve(id);
            if (result != null) return result;
        }
        return null;
    }

    public void Delete(string id)
    {
        foreach (var store in _stores)
            store.Delete(id);
    }

    public async Task<List<KnowledgeSearchResult>> Search(string query, int topK = 10, string? domain = null)
    {
        var results = new List<KnowledgeSearchResult>();
        foreach (var store in _stores)
        {
            var storeResults = await store.Search(query, topK, domain).ConfigureAwait(false);
            results.AddRange(storeResults);
        }
        return results.OrderByDescending(r => r.Score).Take(topK).ToList();
    }

    public List<KnowledgeSearchResult> SearchKeyword(string[] keywords,
        bool caseSensitive = false, int topK = 20)
    {
        var results = new List<KnowledgeSearchResult>();
        foreach (var store in _stores)
        {
            var storeResults = store.SearchKeyword(keywords, caseSensitive, topK);
            results.AddRange(storeResults);
        }
        return results.OrderByDescending(r => r.Score).Take(topK).ToList();
    }

    public List<DocumentEntity> ListDocuments(string? domain = null, string? category = null)
    {
        return _stores.SelectMany(s => s.ListDocuments(domain, category)).ToList();
    }

    public async Task<DocumentStoreStats> GetStats()
    {
        var stats = new DocumentStoreStats();
        foreach (var store in _stores)
        {
            var s = await store.GetStats().ConfigureAwait(false);
            stats.TotalDocuments += s.TotalDocuments;
            stats.TotalChunks += s.TotalChunks;
            stats.TotalRelations += s.TotalRelations;
        }
        return stats;
    }
}
