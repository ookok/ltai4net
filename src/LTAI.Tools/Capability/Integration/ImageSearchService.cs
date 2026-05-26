using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tools.Integration;

public sealed record ImageSearchResult(
    string Id,
    string Url,
    string ThumbUrl,
    string Description,
    string Author,
    int Width,
    int Height,
    string Source);

public sealed class ImageSearchService : IDisposable
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ILogger<ImageSearchService> _logger;

    public string UnsplashAccessKey { get; set; } = "";
    public string PixabayApiKey { get; set; } = "";

    public ImageSearchService(ILogger<ImageSearchService>? logger = null)
    {
        _logger = logger ?? NullLogger<ImageSearchService>.Instance;
    }

    public void Dispose() { }

    public async Task<List<ImageSearchResult>> SearchAsync(string query, int count = 10, string source = "unsplash")
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<ImageSearchResult>();

        return source.ToLowerInvariant() switch
        {
            "pixabay" => await SearchPixabayAsync(query, count),
            _ => await SearchUnsplashAsync(query, count)
        };
    }

    private async Task<List<ImageSearchResult>> SearchUnsplashAsync(string query, int count)
    {
        if (string.IsNullOrWhiteSpace(UnsplashAccessKey))
            return new List<ImageSearchResult>();

        try
        {
            var url = $"https://api.unsplash.com/search/photos?query={Uri.EscapeDataString(query)}&per_page={Math.Min(count, 30)}&client_id={UnsplashAccessKey}";
            var json = await _http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            var results = new List<ImageSearchResult>();
            if (doc.RootElement.TryGetProperty("results", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    results.Add(new ImageSearchResult(
                        item.GetProperty("id").GetString() ?? "",
                        item.GetProperty("urls").GetProperty("regular").GetString() ?? "",
                        item.GetProperty("urls").GetProperty("thumb").GetString() ?? "",
                        item.TryGetProperty("description", out var d) && d.ValueKind != JsonValueKind.Null ? d.GetString() ?? "" : item.TryGetProperty("alt_description", out var ad) ? ad.GetString() ?? "" : "",
                        item.GetProperty("user").GetProperty("name").GetString() ?? "",
                        item.GetProperty("width").GetInt32(),
                        item.GetProperty("height").GetInt32(),
                        "unsplash"
                    ));
                }
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unsplash search failed");
            return new List<ImageSearchResult>();
        }
    }

    private async Task<List<ImageSearchResult>> SearchPixabayAsync(string query, int count)
    {
        if (string.IsNullOrWhiteSpace(PixabayApiKey))
            return new List<ImageSearchResult>();

        try
        {
            var url = $"https://pixabay.com/api/?key={PixabayApiKey}&q={Uri.EscapeDataString(query)}&per_page={Math.Min(count, 200)}&image_type=photo";
            var json = await _http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            var results = new List<ImageSearchResult>();
            if (doc.RootElement.TryGetProperty("hits", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    results.Add(new ImageSearchResult(
                        item.GetProperty("id").GetInt32().ToString(),
                        item.GetProperty("largeImageURL").GetString() ?? "",
                        item.GetProperty("previewURL").GetString() ?? "",
                        item.TryGetProperty("tags", out var tags) ? tags.GetString() ?? "" : "",
                        item.GetProperty("user").GetString() ?? "",
                        item.GetProperty("imageWidth").GetInt32(),
                        item.GetProperty("imageHeight").GetInt32(),
                        "pixabay"
                    ));
                }
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pixabay search failed");
            return new List<ImageSearchResult>();
        }
    }
}
