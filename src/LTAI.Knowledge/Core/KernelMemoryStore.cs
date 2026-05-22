using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory;

namespace LTAI.Knowledge.Core;

public sealed class KernelMemoryStore
{
    private readonly IKernelMemory _memory;
    private readonly ILogger<KernelMemoryStore> _logger;
    private readonly List<string> _importedDocIds = new();

    public KernelMemoryStore(IKernelMemory memory, ILogger<KernelMemoryStore> logger)
    {
        _memory = memory;
        _logger = logger;
    }

    public async Task<string> ImportDocumentAsync(
        string content,
        string? documentId = null,
        Dictionary<string, object?>? tags = null,
        CancellationToken cancellationToken = default)
    {
        documentId ??= $"doc_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";

        var tagCollection = new TagCollection();
        if (tags != null)
        {
            foreach (var (k, v) in tags)
                tagCollection.Add(k, v?.ToString() ?? "");
        }

        var docId = await _memory.ImportTextAsync(
            content,
            documentId: documentId,
            tags: tagCollection,
            cancellationToken: cancellationToken);

        _importedDocIds.Add(docId);
        _logger.LogInformation("Document imported: {DocId}, length: {Length}", docId, content.Length);

        return docId;
    }

    public async Task<MemoryAnswer> AskAsync(
        string question,
        string? index = null,
        MemoryFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var answer = await _memory.AskAsync(
            question,
            index: index,
            filter: filter,
            cancellationToken: cancellationToken);

        _logger.LogInformation("KM ask: {Question}",
            question[..Math.Min(question.Length, 100)]);

        return answer;
    }

    public async Task<SearchResult> SearchAsync(
        string query,
        string? index = null,
        int limit = 5,
        MemoryFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _memory.SearchAsync(
            query,
            index: index,
            limit: limit,
            filter: filter,
            cancellationToken: cancellationToken);

        _logger.LogInformation("KM search: {Query}, results: {Count}",
            query[..Math.Min(query.Length, 100)], result.Results.Count);

        return result;
    }

    public async Task DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await _memory.DeleteDocumentAsync(documentId, cancellationToken: cancellationToken);
        _importedDocIds.Remove(documentId);
        _logger.LogInformation("Document deleted: {DocId}", documentId);
    }

    public IReadOnlyList<string> ImportedDocumentIds => _importedDocIds.AsReadOnly();
}
