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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<LTAIOptions> _options;
    private readonly ILogger<ProviderEngine> _logger;
    private decimal _dailySpent;
    private DateTime _lastResetUtc = DateTime.UtcNow.Date;
    private readonly object _budgetLock = new();
    private int _consecutiveFailures;
    private DateTime _circuitOpenUntil;
    private const int MaxRetries = 3;
    private const int CircuitBreakerThreshold = 5;
    private static readonly TimeSpan CircuitCooldown = TimeSpan.FromSeconds(30);

    public ProviderEngine(IHttpClientFactory httpClientFactory, IOptions<LTAIOptions> options, ILogger<ProviderEngine> logger)
    {
        _httpClientFactory = httpClientFactory;
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

        var chatPath = aiConfig.ChatCompletionsPath;
        var json = JsonSerializer.Serialize(request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{config.Endpoint.TrimEnd('/')}{chatPath}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {config.ApiKey}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(options.TimeoutMs);

        var response = await _httpClientFactory.CreateClient().SendAsync(httpRequest, cts.Token);

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

        var chatPath = aiConfig.ChatCompletionsPath;
        var json = JsonSerializer.Serialize(request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{config.Endpoint.TrimEnd('/')}{chatPath}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Add("Authorization", $"Bearer {config.ApiKey}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(options.TimeoutMs);

        var response = await _httpClientFactory.CreateClient().SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token);
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
        if (DateTime.UtcNow < _circuitOpenUntil)
            throw new InvalidOperationException($"Circuit breaker open until {_circuitOpenUntil:O}");

        Exception? lastEx = null;
        var currentPrompt = prompt;

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var result = await ChatAsync(currentPrompt, options, ct);
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, result));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastEx = ex;
                var failures = Interlocked.Increment(ref _consecutiveFailures);
                if (failures >= CircuitBreakerThreshold)
                {
                    _circuitOpenUntil = DateTime.UtcNow + CircuitCooldown;
                    _logger.LogError("Circuit breaker OPEN after {Failures} failures", failures);
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    throw new InvalidOperationException($"Circuit breaker open: {ex.Message}", ex);
                }
                if (attempt < MaxRetries)
                {
                    var delayMs = 200 * (int)Math.Pow(2, attempt - 1);
                    var nudge = BuildRetryNudge(ex);
                    if (!string.IsNullOrWhiteSpace(nudge))
                        currentPrompt = $"{prompt}\n\n[Diagnostic from attempt {attempt}: {nudge}]";
                    _logger.LogWarning("Retry {A}/{M}: {Error}", attempt, MaxRetries, ex.Message);
                    await Task.Delay(delayMs, ct);
                }
            }
        }
        throw lastEx!;
    }

    private static string BuildRetryNudge(Exception ex)
    {
        if (ex is HttpRequestException h && h.Message.Contains("429"))
            return "Rate-limited. Your request was too frequent. Simplify if possible.";
        if (ex is InvalidOperationException i && i.Message.Contains("budget"))
            return "Daily budget exceeded. Use a flash-tier model or wait until midnight.";
        if (ex is InvalidOperationException i2 && i2.Message.Contains("circuit"))
            return "Provider circuit breaker open. Try again in 30 seconds.";
        if (ex is TaskCanceledException)
            return "Request timed out. The model may be overloaded. Consider simplifying the prompt.";
        if (ex.Message.Contains("JSON") || ex.Message.Contains("parse"))
            return "Previous response had invalid JSON format. Ensure all string values are double-quoted.";
        return $"Previous attempt failed: {ex.Message}. Please retry with a corrected approach.";
    }

    void IDisposable.Dispose()
    {
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
            var today = DateTime.UtcNow.Date;
            if (_lastResetUtc < today)
            {
                _dailySpent = 0;
                _lastResetUtc = today;
                _logger.LogInformation("Budget reset for {Date}", today);
            }

            if (_dailySpent >= ai.DailyBudgetUsd)
                throw new InvalidOperationException(
                    $"Daily budget exceeded: {_dailySpent:F2} / {ai.DailyBudgetUsd} USD. Reset at midnight UTC.");
        }
    }

    private void EstimateCost(int tokens)
    {
        var pricing = _options.Value.ModelPricing;
        var modelKey = _options.Value.AI.L2.Model;

        var inputCostPer1M = pricing.InputPer1M.GetValueOrDefault(modelKey, pricing.InputPer1M.GetValueOrDefault("default", 0.50));
        var outputCostPer1M = pricing.OutputPer1M.GetValueOrDefault(modelKey, pricing.OutputPer1M.GetValueOrDefault("default", 2.00));

        var inputTokens = (int)(tokens * 0.3);
        var outputTokens = tokens - inputTokens;
        
        var inputCost = inputTokens / 1_000_000.0 * inputCostPer1M;
        var outputCost = outputTokens / 1_000_000.0 * outputCostPer1M;
        var cost = inputCost + outputCost;

        lock (_budgetLock)
        {
            _dailySpent += (decimal)cost;
        }
        _logger.LogDebug("Tokens used: {Tokens} (in: {InTokens}, out: {OutTokens}), model: {Model}, cost: ${Cost:F4}, daily total: ${Daily:F2}",
            tokens, inputTokens, outputTokens, modelKey, cost, _dailySpent);
    }
}
