using Microsoft.Extensions.AI;

namespace LTAI.Knowledge.Vector.Embedding;

public sealed class TaskAwareEmbeddingGeneratorDecorator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _inner;
    private readonly string _defaultPrefix;

    public TaskAwareEmbeddingGeneratorDecorator(
        IEmbeddingGenerator<string, Embedding<float>> inner,
        string defaultPrefix = "passage: ")
    {
        _inner = inner;
        _defaultPrefix = defaultPrefix;
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var prefixed = values.Select(v => _defaultPrefix + v).ToList();
        return await _inner.GenerateAsync(prefixed, options, cancellationToken).ConfigureAwait(false);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _inner.GetService(serviceType, serviceKey);

    void IDisposable.Dispose()
    {
        if (_inner is IDisposable d)
            d.Dispose();
    }
}
