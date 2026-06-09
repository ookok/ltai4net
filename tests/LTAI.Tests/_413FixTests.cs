using System.Diagnostics;
using Xunit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.AI;
using Xunit.Abstractions;

namespace LTAI.Tests;

/// <summary>
/// Tests HTTP 413 fix: verifies a simple message does NOT get "413 Request Entity Too Large".
/// Uses MultiProviderChatClient directly (bypasses agent build failures).
/// </summary>
[Trait("Category", "Integration")]
public sealed class _413FixTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly ITestOutputHelper _output;

    public _413FixTests(ITestOutputHelper output)
    {
        _output = output;
        output.WriteLine("Building DI for 413 test...");

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();
        services.Configure<LTAIOptions>(config.GetSection("LTAI"));
        services.AddLTAICore(enableOpenTelemetry: false);
        services.AddLTAIAI();

        _sp = services.BuildServiceProvider();

        var router = _sp.GetRequiredService<MultiProviderChatClient>();
        var key = SecretManager.Get("DEEPSEEK_API_KEY");
        if (!string.IsNullOrEmpty(key))
        {
            router.Register("l1", OpenAIChatClientFactory.Create(
                "https://api.deepseek.com", "deepseek-chat", key));
            output.WriteLine("DeepSeek key found, registered as l1");
        }
    }

    public void Dispose()
    {
        _ = _sp.DisposeAsync();
    }

    /// <summary>Sends a minimal message and verifies no 413 error.</summary>
    [Fact]
    public async Task SimpleMessage_No413()
    {
        var router = _sp.GetRequiredService<MultiProviderChatClient>();
        var opts = _sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LTAIOptions>>().Value;
        _output.WriteLine($"Providers: {string.Join(", ", router.RegisteredProviders)}");

        var msg = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant."),
            new(ChatRole.User, "Hi, respond in one short sentence.")
        };
        var chatOpts = new ChatOptions { Temperature = 0.1f };

        // Add a dummy tool to test realistic payload size
        chatOpts.Tools = [AIFunctionFactory.Create(() => "ok", "test_tool")];

        _output.WriteLine($"Sending message...");
        var sw = Stopwatch.StartNew();
        var resp = await router.GetResponseAsync(msg, chatOpts);
        sw.Stop();

        var result = resp.Messages?.LastOrDefault()?.Text ?? "(empty)";
        _output.WriteLine($"Response ({sw.ElapsedMilliseconds}ms): {result[..Math.Min(result.Length, 300)]}");
        if (resp.Usage != null)
            _output.WriteLine($"Tokens: input={resp.Usage.InputTokenCount}, output={resp.Usage.OutputTokenCount}");

        Assert.DoesNotContain("413", result);
        Assert.DoesNotContain("No providers available", result);
        Assert.False(result.Contains("failed"), $"Got failure: {result}");
    }
}
