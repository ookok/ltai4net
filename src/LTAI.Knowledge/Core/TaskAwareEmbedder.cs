using Microsoft.Extensions.AI;

namespace LTAI.Knowledge.Core;

public static class TaskAwareEmbedder
{
    public static string QueryPrefix { get; set; } = "query: ";
    public static string DocumentPrefix { get; set; } = "passage: ";

    public static async Task<Embedding<float>> EmbedQueryAsync(
        string query,
        IEmbeddingGenerator<string, Embedding<float>> embedding,
        CancellationToken cancellationToken = default)
    {
        var result = await embedding.GenerateAsync(
            new[] { QueryPrefix + query },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result[0];
    }

    public static async Task<Embedding<float>> EmbedDocumentAsync(
        string document,
        IEmbeddingGenerator<string, Embedding<float>> embedding,
        CancellationToken cancellationToken = default)
    {
        var result = await embedding.GenerateAsync(
            new[] { DocumentPrefix + document },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result[0];
    }
}
