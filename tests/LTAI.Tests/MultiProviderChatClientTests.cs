using Xunit;
using LTAI.AI;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;

namespace LTAI.Tests;

public sealed class MultiProviderChatClientTests
{
    private static readonly LTAIOptions DefaultOpts = new()
    {
        AI = new AIConfig
        {
            DefaultProvider = "l1",
            GlobalTokenBudget = 1_000_000,
            PerUserTokenBudget = 200_000,
            DegradationChain = new()
            {
                ["l1"] = "l2",
                ["l2"] = "fallback",
            },
        }
    };

    [Fact]
    public async Task DegradationChain_PrimaryFails_FallsBack()
    {
        var router = new MultiProviderChatClient(DefaultOpts);
        router.Register("l2", new EchoChatClient("fallback-ok"));
        var resp = await router.GetResponseAsync([new ChatMessage(ChatRole.User, "unique-fallback-primary")]);
        var text = resp.Messages?.LastOrDefault()?.Text ?? "";
        Assert.Contains("fallback-ok", text);
    }

    [Fact]
    public async Task DegradationChain_MultiLevel_FallsBack()
    {
        var router = new MultiProviderChatClient(DefaultOpts);
        router.Register("fallback", new EchoChatClient("last-resort"));
        var resp = await router.GetResponseAsync([new ChatMessage(ChatRole.User, "unique-fallback-multi")]);
        var text = resp.Messages?.LastOrDefault()?.Text ?? "";
        Assert.Contains("last-resort", text);
    }

    [Fact]
    public async Task DegradationChain_NoRegistered_ReturnsError()
    {
        var opts = new LTAIOptions
        {
            AI = new AIConfig
            {
                DefaultProvider = "missing",
                GlobalTokenBudget = 1_000_000,
                PerUserTokenBudget = 200_000,
            }
        };
        var router = new MultiProviderChatClient(opts);
        var resp = await router.GetResponseAsync([new ChatMessage(ChatRole.User, "unique-no-providers")]);
        var text = resp.Messages?.LastOrDefault()?.Text ?? "";
        Assert.Contains("All providers failed", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetResponseAsync_NoProviders_ReturnsError()
    {
        var router = new MultiProviderChatClient(new LTAIOptions());
        var resp = await router.GetResponseAsync([new ChatMessage(ChatRole.User, "unique-no-registered")]);
        var text = resp.Messages?.LastOrDefault()?.Text ?? "";
        Assert.Contains("All providers failed", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetResponseAsync_SingleProvider_Succeeds()
    {
        var router = new MultiProviderChatClient(DefaultOpts);
        router.Register("l1", new EchoChatClient("hello world"));
        var resp = await router.GetResponseAsync([new ChatMessage(ChatRole.User, "unique-single-provider")]);
        var text = resp.Messages?.LastOrDefault()?.Text ?? "";
        Assert.Equal("hello world", text);
    }

    [Fact]
    public async Task GetResponseAsync_PicksCorrectProviderByModelId()
    {
        var router = new MultiProviderChatClient(DefaultOpts);
        router.Register("l1", new EchoChatClient("from-l1"));
        router.Register("l2", new EchoChatClient("from-l2"));
        var resp = await router.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "unique-pick-by-model")],
            new ChatOptions { ModelId = "l2" });
        Assert.Contains("from-l2", resp.Messages?.LastOrDefault()?.Text ?? "");
    }

    [Fact]
    public async Task CircuitBreaker_3Failures_TriggersCooldown()
    {
        var router = new MultiProviderChatClient(DefaultOpts);
        router.Register("l1", new FaultyChatClient(new InvalidOperationException("transient")));
        router.Register("l2", new EchoChatClient("after-cooldown"));

        for (int i = 0; i < 3; i++)
            await router.GetResponseAsync([new ChatMessage(ChatRole.User, "unique-cb-" + i)]);

        var resp = await router.GetResponseAsync([new ChatMessage(ChatRole.User, "unique-cb-after")]);
        var text = resp.Messages?.LastOrDefault()?.Text ?? "";
        Assert.Contains("after-cooldown", text);
    }

    [Fact]
    public async Task Degradation_SkipsCooldownProvider()
    {
        var router = new MultiProviderChatClient(DefaultOpts);
        router.Register("l1", new FaultyChatClient(new InvalidOperationException("failing")));
        router.Register("l2", new EchoChatClient("skipped-cooldown"));

        for (int i = 0; i < 3; i++)
            await router.GetResponseAsync([new ChatMessage(ChatRole.User, "unique-sk-" + i)]);

        var resp = await router.GetResponseAsync([new ChatMessage(ChatRole.User, "unique-sk-after")]);
        var text = resp.Messages?.LastOrDefault()?.Text ?? "";
        Assert.Contains("skipped-cooldown", text);
    }

    [Fact]
    public async Task ResponseCache_HitReturnsCachedValue()
    {
        var router = new MultiProviderChatClient(DefaultOpts);
        var provider = new CountingChatClient();
        router.Register("l1", provider);

        var resp1 = await router.GetResponseAsync([new ChatMessage(ChatRole.User, "unique-cache-test")]);
        Assert.Contains("count-1", resp1.Messages?.LastOrDefault()?.Text ?? "");

        var resp2 = await router.GetResponseAsync([new ChatMessage(ChatRole.User, "unique-cache-test")]);
        Assert.Contains("count-1", resp2.Messages?.LastOrDefault()?.Text ?? "");

        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task ResponseCache_DifferentInput_DifferentCacheEntry()
    {
        var router = new MultiProviderChatClient(DefaultOpts);
        var provider = new CountingChatClient();
        router.Register("l1", provider);

        await router.GetResponseAsync([new ChatMessage(ChatRole.User, "unique-cache-1")]);
        await router.GetResponseAsync([new ChatMessage(ChatRole.User, "unique-cache-2")]);

        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task RegisteredProviders_InitiallyEmpty()
    {
        var router = new MultiProviderChatClient(new LTAIOptions());
        Assert.Empty(router.RegisteredProviders);
    }

    [Fact]
    public async Task RegisteredProviders_AfterRegistration_ContainsKey()
    {
        var router = new MultiProviderChatClient(new LTAIOptions());
        router.Register("my-provider", new EchoChatClient("ok"));
        Assert.Contains("my-provider", router.RegisteredProviders);
    }

    [Fact]
    public async Task ActiveProvider_DefaultValue()
    {
        var router = new MultiProviderChatClient(new LTAIOptions
        {
            AI = new AIConfig { DefaultProvider = "custom-default", GlobalTokenBudget = 1_000_000, PerUserTokenBudget = 200_000 }
        });
        Assert.Equal("custom-default", router.ActiveProvider);
    }

    [Fact]
    public async Task ActiveProvider_CanChange()
    {
        var opts = new LTAIOptions { AI = new AIConfig { DefaultProvider = "old", GlobalTokenBudget = 1_000_000, PerUserTokenBudget = 200_000 } };
        var router = new MultiProviderChatClient(opts);
        router.ActiveProvider = "new-provider";
        Assert.Equal("new-provider", router.ActiveProvider);
    }

    [Fact]
    public async Task Streaming_NoProviders_ReturnsError()
    {
        var router = new MultiProviderChatClient(new LTAIOptions());
        var results = new List<ChatResponseUpdate>();
        await foreach (var update in router.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "unique-stream-no-prov")]))
        {
            results.Add(update);
        }
        Assert.NotEmpty(results);
        var text = string.Concat(results.Select(r => r.Text ?? ""));
        Assert.Contains("No providers available", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Streaming_SingleProvider_Succeeds()
    {
        var router = new MultiProviderChatClient(DefaultOpts);
        router.Register("l1", new EchoChatClient("stream response"));
        var results = new List<ChatResponseUpdate>();
        await foreach (var update in router.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "unique-stream-ok")]))
        {
            results.Add(update);
        }
        Assert.NotEmpty(results);
        var text = string.Concat(results.Select(r => r.Text ?? ""));
        Assert.Contains("stream response", text);
    }

    [Fact]
    public async Task Streaming_FallbackOnFailure()
    {
        var router = new MultiProviderChatClient(DefaultOpts);
        router.Register("l1", new FaultyChatClient(new InvalidOperationException("stream fail")));
        router.Register("l2", new EchoChatClient("stream fallback"));

        var results = new List<ChatResponseUpdate>();
        await foreach (var update in router.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "unique-stream-fail")]))
        {
            results.Add(update);
        }
        var text = string.Concat(results.Select(r => r.Text ?? ""));
        Assert.Contains("stream fallback", text);
    }
}

file sealed class FaultyChatClient : IChatClient
{
    private readonly Exception _exception;
    public FaultyChatClient(Exception exception) => _exception = exception;
    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public object? GetService(Type serviceType, string? serviceKey) => null;
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromException<ChatResponse>(_exception);
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        var ex = _exception;
        if (ex != null) throw ex;
        yield break;
    }
}

file sealed class CountingChatClient : IChatClient
{
    private int _callCount;
    public int CallCount => _callCount;
    public void Dispose() { }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public object? GetService(Type serviceType, string? serviceKey) => null;
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var count = Interlocked.Increment(ref _callCount);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"count-{count}")));
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        var count = Interlocked.Increment(ref _callCount);
        yield return new ChatResponseUpdate(ChatRole.Assistant, $"count-{count}");
    }
}
