using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LTAI.Core.Configuration;
using LTAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI.Providers;

public sealed class ProviderEngine : IProviderEngine
{
    private readonly HttpClient _http;
    private readonly IOptions<LTAIOptions> _options;
    private readonly ILogger<ProviderEngine> _logger;
    private decimal _dailySpent;

    public ProviderEngine(IOptions<LTAIOptions> options, ILogger<ProviderEngine> logger)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _options = options;
        _logger = logger;
    }

    public async Task<string> ChatAsync(string prompt, LLMChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new LLMChatOptions();
        var aiConfig = _options.Value.AI;
        var model = options.Model ?? aiConfig.DeepModel;

        CheckBudget();

        if (!aiConfig.Providers.TryGetValue(model, out var config))
        {
            config = aiConfig.Providers.Values.FirstOrDefault()
                ?? throw new InvalidOperationException($"No provider configured for model: {model}");
        }

        var temperature = options.Temperature > 0 ? options.Temperature : aiConfig.DefaultTemperature;
        var maxTokens = options.MaxTokens > 0 ? options.MaxTokens : aiConfig.MaxTokens;

        var request = new
        {
            model = config.Model,
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            temperature,
            max_tokens = maxTokens,
            stream = false
        };

        var json = JsonSerializer.Serialize(request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{config.Endpoint.TrimEnd('/')}/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {config.ApiKey}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(options.TimeoutMs);

        var response = await _http.SendAsync(httpRequest, cts.Token);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Provider API error: {Status} {Error}", (int)response.StatusCode, errorBody);
            throw new HttpRequestException($"Provider returned {(int)response.StatusCode}: {errorBody[..Math.Min(errorBody.Length, 500)]}");
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var content = choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";

            if (root.TryGetProperty("usage", out var usage))
            {
                var tokens = usage.TryGetProperty("total_tokens", out var t) ? t.GetInt32() : 0;
                EstimateCost(tokens);
            }

            return content;
        }

        return string.Empty;
    }

    public async IAsyncEnumerable<string> StreamAsync(string prompt, LLMChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= new LLMChatOptions();
        var aiConfig = _options.Value.AI;
        var model = options.Model ?? aiConfig.DeepModel;

        CheckBudget();

        if (!aiConfig.Providers.TryGetValue(model, out var config))
        {
            config = aiConfig.Providers.Values.FirstOrDefault()
                ?? throw new InvalidOperationException($"No provider configured for model: {model}");
        }

        var temperature = options.Temperature > 0 ? options.Temperature : aiConfig.DefaultTemperature;

        var request = new
        {
            model = config.Model,
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            temperature,
            max_tokens = options.MaxTokens > 0 ? options.MaxTokens : aiConfig.MaxTokens,
            stream = true
        };

        var json = JsonSerializer.Serialize(request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{config.Endpoint.TrimEnd('/')}/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {config.ApiKey}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(options.TimeoutMs);

        var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        await foreach (var text in ReadStreamAsync(reader, cancellationToken))
        {
            if (!string.IsNullOrEmpty(text))
                yield return text;
        }
    }

    private static async IAsyncEnumerable<string> ReadStreamAsync(StreamReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null)
                break;
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
                continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]")
                break;

            var text = ParseStreamChunk(data);
            if (!string.IsNullOrEmpty(text))
                yield return text;
        }
    }

    private static string? ParseStreamChunk(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var content))
                    return content.GetString();
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckBudget()
    {
        if (_dailySpent >= _options.Value.AI.DailyBudgetUsd)
            throw new InvalidOperationException($"Daily budget exceeded: ${_dailySpent:F2}/{_options.Value.AI.DailyBudgetUsd:F2}");
    }

    private void EstimateCost(int tokens)
    {
        _dailySpent += tokens * 0.000002m;
    }
}
