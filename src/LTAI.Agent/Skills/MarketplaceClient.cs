using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Skills;

public sealed class MarketplaceClient
{
    private readonly HttpClient _http;
    private readonly ILogger<MarketplaceClient> _logger;
    private readonly string _baseUrl;
    private readonly ConcurrentQueue<DateTime> _callTimestamps = new();
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);
    private const int MaxCallsPerWindow = 100;
    private static readonly SemaphoreSlim _rateGate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public MarketplaceClient(HttpClient http, ILogger<MarketplaceClient> logger, string? baseUrl = null)
    {
        _http = http;
        _logger = logger;
        _baseUrl = baseUrl ?? "https://skills.ltai.dev/api/v1";
    }

    public async Task<List<MarketplaceSearchResult>> SearchAsync(string query, string? domain = null,
        SkillLayer? layer = null, int page = 0, int pageSize = 20, CancellationToken ct = default)
    {
        if (!await CheckRateLimitAsync(ct)) return new();

        try
        {
            var url = $"{_baseUrl}/skills?q={Uri.EscapeDataString(query)}&page={page}&page_size={pageSize}";
            if (domain != null) url += $"&domain={Uri.EscapeDataString(domain)}";
            if (layer.HasValue) url += $"&layer={(int)layer.Value}";

            var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Marketplace search failed: {Status} for query '{Query}'", response.StatusCode, query);
                return new();
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var results = JsonSerializer.Deserialize<List<MarketplaceSearchResult>>(json, JsonOptions);
            return results ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to search marketplace for '{Query}'", query);
            return new();
        }
    }

    public async Task<MarketplaceSearchResult?> GetMetadataAsync(string marketplaceId, CancellationToken ct = default)
    {
        if (!await CheckRateLimitAsync(ct)) return null;

        try
        {
            var url = $"{_baseUrl}/skills/{Uri.EscapeDataString(marketplaceId)}";
            var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Marketplace metadata fetch failed: {Status} for '{Id}'", response.StatusCode, marketplaceId);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<MarketplaceSearchResult>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get metadata for '{Id}'", marketplaceId);
            return null;
        }
    }

    public async Task<string> DownloadAsync(string marketplaceId, CancellationToken ct = default)
    {
        if (!await CheckRateLimitAsync(ct)) return "";

        try
        {
            var url = $"{_baseUrl}/skills/{Uri.EscapeDataString(marketplaceId)}/download";
            var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Marketplace download failed: {Status} for '{Id}'", response.StatusCode, marketplaceId);
                return "";
            }

            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download skill '{Id}'", marketplaceId);
            return "";
        }
    }

    public async Task<string?> CheckForUpdateAsync(string marketplaceId, string currentVersion, CancellationToken ct = default)
    {
        if (!await CheckRateLimitAsync(ct)) return null;

        try
        {
            var url = $"{_baseUrl}/skills/{Uri.EscapeDataString(marketplaceId)}/version";
            var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Marketplace version check failed: {Status} for '{Id}'", response.StatusCode, marketplaceId);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var latestVersion = doc.RootElement.GetProperty("version").GetString();
            if (latestVersion != null && latestVersion != currentVersion)
                return latestVersion;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check for update for '{Id}'", marketplaceId);
            return null;
        }
    }

    public async Task<bool> RateAsync(string marketplaceId, int rating, string? review = null, CancellationToken ct = default)
    {
        if (!await CheckRateLimitAsync(ct)) return false;

        try
        {
            var url = $"{_baseUrl}/skills/{Uri.EscapeDataString(marketplaceId)}/rate";
            var payload = new { rating, review };
            var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions),
                System.Text.Encoding.UTF8, "application/json");

            var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Marketplace rating failed: {Status} for '{Id}'", response.StatusCode, marketplaceId);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rate skill '{Id}'", marketplaceId);
            return false;
        }
    }

    private async Task<bool> CheckRateLimitAsync(CancellationToken ct)
    {
        await _rateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            while (_callTimestamps.TryPeek(out var oldest) && (now - oldest) > RateWindow)
                _callTimestamps.TryDequeue(out _);

            if (_callTimestamps.Count >= MaxCallsPerWindow)
            {
                _logger.LogWarning("MarketplaceClient rate limit reached ({Count}/{Max} calls per minute)",
                    _callTimestamps.Count, MaxCallsPerWindow);
                return false;
            }

            _callTimestamps.Enqueue(now);
            return true;
        }
        finally
        {
            _rateGate.Release();
        }
    }
}
