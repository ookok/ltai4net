using LTAI.Knowledge.Core.Models;

namespace LTAI.Knowledge.Core;

/// <summary>
/// Unified interface for knowledge storage, retrieval, and search.
/// Implemented by KnowledgeBase, KnowledgeGraph, and DualMemoryStore
/// to enable cross-system synchronization and eventual consolidation.
/// </summary>
public interface IKnowledgeStore
{
    /// <summary>Add a knowledge entry. Returns the assigned ID.</summary>
    string AddKnowledge(
        string title,
        string content,
        string domain = "general",
        string category = "document",
        string source = "manual",
        string author = "system",
        double importance = 0.0,
        bool skipDedup = false,
        bool indexVector = true);

    /// <summary>Retrieve a single document by ID.</summary>
    DocumentEntity? Retrieve(string id);

    /// <summary>Delete a document by ID.</summary>
    void Delete(string id);

    /// <summary>Hybrid semantic+keyword search.</summary>
    Task<List<KnowledgeSearchResult>> Search(string query, int topK = 10, string? domain = null);

    /// <summary>Keyword-only search via FTS5.</summary>
    List<KnowledgeSearchResult> SearchKeyword(string[] keywords, bool caseSensitive = false, int topK = 20);

    /// <summary>List documents by domain/category.</summary>
    List<DocumentEntity> ListDocuments(string? domain = null, string? category = null);

    /// <summary>Get storage statistics.</summary>
    Task<DocumentStoreStats> GetStats();
}
