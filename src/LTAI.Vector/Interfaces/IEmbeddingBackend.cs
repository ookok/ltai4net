namespace LTAI.Vector.Interfaces;

public interface IEmbeddingBackend
{
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);

    int Dimension { get; }
}
