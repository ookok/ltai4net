using LTAI.Knowledge.Vector.Interfaces;
using LTAI.Knowledge.Core.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Knowledge.Core;

public sealed class KnowledgeBase
{
    private readonly DocumentStore _docStore;
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<KnowledgeBase> _logger;
    private const double RrfK = 60.0;

    public KnowledgeBase(
        DocumentStore docStore,
        IVectorStore vectorStore,
        ILogger<KnowledgeBase> logger)
    {
        _docStore = docStore;
        _vectorStore = vectorStore;
        _logger = logger;
    }

    public string AddKnowledge(
        string title,
        string content,
        string domain = "general",
        string category = "document",
        string source = "manual",
        string author = "system",
        double importance = 0.0,
        bool skipDedup = false,
        bool indexVector = true)
    {
        var id = _docStore.AddDocument(title, content, domain, category, source, author, importance);

        if (indexVector)
        {
            Task.Run(async () =>
            {
                try
                {
                    await _docStore.IndexDocumentVectorAsync(id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to index vector for document {Id}", id);
                }
            });
        }

        _logger.LogInformation("Knowledge added: {Id} ({Title})", id, title);
        return id;
    }

    public DocumentEntity? Retrieve(string id)
    {
        return _docStore.GetDocument(id);
    }

    public void Delete(string id)
    {
        _docStore.DeleteDocument(id);
        _logger.LogInformation("Knowledge deleted: {Id}", id);
    }

    public async Task<List<KnowledgeSearchResult>> Search(
        string query,
        int topK = 10,
        string? domain = null)
    {
        return await _docStore.Search(query, domain, topK);
    }

    public async Task<List<KnowledgeSearchResult>> SearchCurrent(string query, int topK = 10)
    {
        return await _docStore.Search(query, null, topK);
    }

    public List<KnowledgeSearchResult> SearchKeyword(
        string[] keywords,
        bool caseSensitive = false,
        int topK = 20)
    {
        var ftsQuery = string.Join(" AND ", keywords.Select(k => $"\"{k}\""));
        return _docStore.SearchFts(ftsQuery, null, topK * 2).Take(topK).ToList();
    }

    public List<DocumentEntity> GetByDomain(string? domain = null)
    {
        return _docStore.ListDocuments(domain);
    }

    public List<DocumentEntity> ListDocuments(string? domain = null, string? category = null)
    {
        return _docStore.ListDocuments(domain, category);
    }

    public List<ChunkInfo> GetChunks(string docId)
    {
        return _docStore.GetChunks(docId).Select(c => new ChunkInfo
        {
            Text = c.Content,
            StartChar = c.StartChar ?? 0
        }).ToList();
    }

    public int AddRelation(
        string sourceId,
        string targetId,
        string relation = "references",
        double weight = 1.0)
    {
        return _docStore.AddRelation(sourceId, targetId, relation, weight);
    }

    public async Task<List<KnowledgeSearchResult>> SearchAsync(
        string query,
        int topK = 10,
        string? domain = null)
    {
        return await Task.Run(() => Search(query, topK, domain));
    }

    public async Task<DocumentStoreStats> GetStats()
    {
        return await _docStore.GetStats();
    }
}
