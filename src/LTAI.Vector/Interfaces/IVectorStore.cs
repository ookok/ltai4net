using LTAI.Vector.Models;

namespace LTAI.Vector.Interfaces;

public interface IVectorStore
{
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);

    Task AddVectorsAsync(IReadOnlyList<(string Id, float[] Vector)> items, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorSearchResult>> SearchSimilarAsync(
        float[] queryVector,
        int topK = 5,
        CancellationToken cancellationToken = default);

    Task DeleteVectorAsync(string docId, CancellationToken cancellationToken = default);

    Task CreateCollectionAsync(string name, CancellationToken cancellationToken = default);

    Task<VectorStoreStats> GetStatsAsync(CancellationToken cancellationToken = default);
}
