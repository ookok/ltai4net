using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LTAI.Core.Configuration;
using LTAI.Core.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI.Providers;

public sealed class ProviderEngine : IChatClient
{
    private readonly HttpClient _http;
    private readonly IOptions<LTAIOptions> _options;
    private readonly ILogger<ProviderEngine> _logger;
    private decimal _dailySpent;
    private readonly object _budgetLock = new();

    public ProviderEngine(IOptions<LTAIOptions> options, ILogger<ProviderEngine> logger)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _options = options;
        _logger = logger;
    }

    public ChatClientMetadata? Metadata
    {
        get
        {
            var ai = _options.Value.AI;
            return new ChatClientMetadata(ai.L2.Model);
        }
    }

    private (ProviderConfig Provider, string ApiModel) ResolveProvider(string modelKey, AIConfig aiConfig)
    {
        if (aiConfig.Providers.TryGetValue(modelKey, out var config))
            return (config, config.Model);

        config = aiConfig.Providers.Values.FirstOrDefault()
            ?? throw new InvalidOperationException($"No provider configured. Requested model key: {modelKey}");

        return (config, modelKey);
    }

    private async Task<string> ChatAsync(string prompt, LLMChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new LLMChatOptions();
        var aiConfig = _options.Value.AI;
        var modelKey = options.Model ?? aiConfig.L2.Model;

        CheckBudget();

        var (config, apiModel) = ResolveProvider(modelKey, aiConfig);

        var temperature = options.Temperature > 0 ? options.Temperature : aiConfig.DefaultTemperature;
        var maxTokens = options.MaxTokens > 0 ? options.MaxTokens : aiConfig.MaxTokens;

        var request = new
        {
            model = apiModel,
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

    private async IAsyncEnumerable<string> StreamAsync(string prompt, LLMChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= new LLMChatOptions();
        var aiConfig = _options.Value.AI;
        var modelKey = options.Model ?? aiConfig.L2.Model;

        CheckBudget();

        var (config, apiModel) = ResolveProvider(modelKey, aiConfig);

        var temperature = options.Temperature > 0 ? options.Temperature : aiConfig.DefaultTemperature;

        var request = new
        {
            model = apiModel,
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

    Task<ChatResponse> IChatClient.GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        var prompt = string.Join("\n", messages.Select(m => m.Text ?? ""));
        var llmOptions = ToLLMChatOptions(options);

        return ChatToResponseAsync(prompt, llmOptions, cancellationToken);
    }

    async IAsyncEnumerable<ChatResponseUpdate> IChatClient.GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prompt = string.Join("\n", messages.Select(m => m.Text ?? ""));
        var llmOptions = ToLLMChatOptions(options);

        await foreach (var chunk in StreamAsync(prompt, llmOptions, cancellationToken))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }
    }

    object? IChatClient.GetService(Type serviceType, object? serviceKey) =>
        serviceType == typeof(ChatClientMetadata) ? Metadata : null;

    private static LLMChatOptions? ToLLMChatOptions(ChatOptions? options)
    {
        if (options == null) return null;
        return new LLMChatOptions
        {
            Temperature = options.Temperature ?? 0.3f,
            MaxTokens = options.MaxOutputTokens ?? 4096,
            Model = options.ModelId
        };
    }

    private async Task<ChatResponse> ChatToResponseAsync(string prompt, LLMChatOptions? options, CancellationToken ct)
    {
        try
        {
            var result = await ChatAsync(prompt, options, ct);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChatResponse error");
            throw;
        }
    }

    void IDisposable.Dispose()
    {
        _http.Dispose();
        GC.SuppressFinalize(this);
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
        }
        catch { /* non-fatal */ }
        return null;
    }

    private void CheckBudget()
    {
        var ai = _options.Value.AI;
        if (ai.DailyBudgetUsd <= 0)
            return;

        lock (_budgetLock)
        {
            if (_dailySpent >= ai.DailyBudgetUsd)
                throw new InvalidOperationException(
                    $"Daily budget exceeded: {_dailySpent:F2} / {ai.DailyBudgetUsd} USD. Reset at midnight UTC.");
        }
    }

    private void EstimateCost(int tokens)
    {
        const double defaultCostPer1K = 0.002;
        var cost = tokens / 1000.0 * defaultCostPer1K;
        lock (_budgetLock)
        {
            _dailySpent += (decimal)cost;
        }
        _logger.LogDebug("Tokens used: {Tokens}, cost: ${Cost:F4}, daily total: ${Daily:F2}",
            tokens, cost, _dailySpent);
    }
}
