using LTAI.Vector.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.KernelMemory.AI;

namespace LTAI.Vector.Embedding;

public sealed class EmbeddingGeneratorAdapter : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IEmbeddingBackend _backend;

    public EmbeddingGeneratorMetadata Metadata { get; }

    public EmbeddingGeneratorAdapter(IEmbeddingBackend backend)
    {
        _backend = backend;
        Metadata = new EmbeddingGeneratorMetadata("LTAI.LocalEmbedding", new Uri("https://localhost"), $"dim-{_backend.Dimension}");
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var texts = values as IReadOnlyList<string> ?? values.ToList();
        var vectors = await _backend.EmbedAsync(texts, cancellationToken);

        var embeddings = vectors.Select(v => new Embedding<float>(v)).ToList();
        return new GeneratedEmbeddings<Embedding<float>>(embeddings);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(IEmbeddingBackend))
            return _backend;
        if (serviceType == typeof(EmbeddingGeneratorMetadata))
            return Metadata;
        return null;
    }

    void IDisposable.Dispose()
    {
        if (_backend is IDisposable d)
            d.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed class KernelMemoryEmbeddingAdapter : ITextEmbeddingGenerator
{
    private readonly IEmbeddingBackend _backend;
    public int MaxTokens => 8192;

    public KernelMemoryEmbeddingAdapter(IEmbeddingBackend backend)
    {
        _backend = backend;
    }

    public int CountTokens(string text) => (int)(text.Length * 0.75);

    public IReadOnlyList<string> GetTokens(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList().AsReadOnly();

    public async Task<Microsoft.KernelMemory.Embedding> GenerateEmbeddingAsync(
        string text, CancellationToken cancellationToken = default)
    {
        var vectors = await _backend.EmbedAsync(new[] { text }, cancellationToken);
        return new Microsoft.KernelMemory.Embedding(vectors[0]);
    }
}
