using LTAI.Vector.Interfaces;
using Microsoft.Extensions.Logging;

namespace LTAI.Vector.Embedding;

public sealed class APIEmbeddingBackend : IEmbeddingBackend, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger<APIEmbeddingBackend> _logger;

    public int Dimension { get; }

    public APIEmbeddingBackend(
        string endpoint,
        string apiKey,
        string model = "text-embedding-3-small",
        int dimension = 1536,
        ILogger<APIEmbeddingBackend>? logger = null)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _endpoint = endpoint.TrimEnd('/');
        _apiKey = apiKey;
        _model = model;
        Dimension = dimension;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<APIEmbeddingBackend>.Instance;
    }

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        return EmbedInternalAsync(texts, cancellationToken);
    }

    private async Task<float[][]> EmbedInternalAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var request = new
        {
            model = _model,
            input = texts
        };

        var json = System.Text.Json.JsonSerializer.Serialize(request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/v1/embeddings")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {_apiKey}");

        var response = await _http.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = System.Text.Json.JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var data = root.GetProperty("data");
        var result = new float[data.GetArrayLength()][];

        for (var i = 0; i < data.GetArrayLength(); i++)
        {
            var embedding = data[i].GetProperty("embedding");
            var vec = new float[embedding.GetArrayLength()];
            for (var j = 0; j < vec.Length; j++)
                vec[j] = embedding[j].GetSingle();
            result[i] = vec;
        }

        return result;
    }

    public void Dispose()
    {
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
