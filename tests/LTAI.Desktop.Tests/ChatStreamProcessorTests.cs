using System.Runtime.CompilerServices;
using LTAI.Core.Session;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;

namespace LTAI.Desktop.Tests;

public sealed class ChatStreamProcessorTests
{
    private sealed class FakeStreamSource : IStreamSource
    {
        private readonly List<string> _tokens;
        private readonly List<string> _toolTokens;
        private readonly Exception? _throwOnAccess;
        public FakeStreamSource(List<string> tokens, List<string>? toolTokens = null, Exception? throwOnAccess = null)
        {
            _tokens = tokens;
            _toolTokens = toolTokens ?? new();
            _throwOnAccess = throwOnAccess;
        }

        public async IAsyncEnumerable<AgentResponseUpdate> ChatStreamingAsync(
            string query, ISessionHandle? sessionHandle, [EnumeratorCancellation] CancellationToken ct)
        {
            if (_throwOnAccess != null) throw _throwOnAccess;
            foreach (var t in _tokens)
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                yield return new AgentResponseUpdate(new ChatResponseUpdate(ChatRole.Assistant, t));
            }
        }

        public object? RenderTool(string token)
            => _toolTokens.Contains(token) ? new object() : null;
    }

    private static Mock<IStreamUiCallbacks> CreateUiMock()
        => new(MockBehavior.Strict);

    [Fact]
    public async Task SimpleText_ReturnsFullResponse()
    {
        var source = new FakeStreamSource(["Hello, world!"]);
        var ui = CreateUiMock();
        ui.Setup(u => u.OnFirstToken());
        ui.Setup(u => u.OnComplete());

        var processor = new ChatStreamProcessor(source, ui.Object);
        var result = await processor.ProcessAsync("test", null, CancellationToken.None);

        Assert.False(result.Cancelled);
        Assert.Equal("Hello, world!", result.FullResponse);
        Assert.Null(result.ThinkingText);
        Assert.Empty(result.ToolTokens);
        ui.VerifyAll();
    }

    [Fact]
    public async Task MultipleTokens_ConcatenatesCorrectly()
    {
        var source = new FakeStreamSource(["Hello, ", "world", "!"]);
        var ui = CreateUiMock();
        ui.Setup(u => u.OnFirstToken());
        ui.Setup(u => u.OnComplete());

        var processor = new ChatStreamProcessor(source, ui.Object);
        var result = await processor.ProcessAsync("test", null, CancellationToken.None);

        Assert.Equal("Hello, world!", result.FullResponse);
    }

    [Fact]
    public async Task ThinkingTags_ExtractsCorrectly()
    {
        var source = new FakeStreamSource([
            "<thinking>",
            "Let me analyze this...",
            "</thinking>",
            "The answer is 42."
        ]);
        var ui = CreateUiMock();
        ui.Setup(u => u.OnThinkingStart());
        ui.Setup(u => u.OnThinkingUpdate("Let me analyze this..."));
        ui.Setup(u => u.OnThinkingUpdate("Let me analyze this..."));
        ui.Setup(u => u.OnFirstToken());
        ui.Setup(u => u.OnComplete());

        var processor = new ChatStreamProcessor(source, ui.Object);
        var result = await processor.ProcessAsync("test", null, CancellationToken.None);

        Assert.Equal("The answer is 42.", result.FullResponse);
        Assert.Equal("Let me analyze this...", result.ThinkingText);
    }

    [Fact]
    public async Task ToolTokens_AreTracked()
    {
        var toolToken = "正在调用 Edit...";
        var source = new FakeStreamSource(
            ["Some text", toolToken, "More text"],
            toolTokens: [toolToken]);
        var ui = CreateUiMock();
        ui.Setup(u => u.OnFirstToken());
        ui.Setup(u => u.OnToolToken(toolToken));
        ui.Setup(u => u.OnComplete());

        var processor = new ChatStreamProcessor(source, ui.Object);
        var result = await processor.ProcessAsync("test", null, CancellationToken.None);

        Assert.Contains(toolToken, result.ToolTokens);
    }

    [Fact]
    public async Task Cancellation_ReturnsCancelledResult()
    {
        using var cts = new CancellationTokenSource();
        // Cancel before starting to ensure deterministic test
        cts.Cancel();
        var source = new FakeStreamSource(["part1", "part2", "part3"]);
        var ui = CreateUiMock();
        ui.Setup(u => u.OnCancelled());

        var processor = new ChatStreamProcessor(source, ui.Object);
        var result = await processor.ProcessAsync("test", null, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Contains("[cancelled]", result.FullResponse);
    }

    [Fact]
    public async Task Exception_ReturnsErrorResult()
    {
        var source = new FakeStreamSource([], throwOnAccess: new InvalidOperationException("test error"));
        var ui = CreateUiMock();
        ui.Setup(u => u.OnError("test error"));

        var processor = new ChatStreamProcessor(source, ui.Object);
        var result = await processor.ProcessAsync("test", null, CancellationToken.None);
        Assert.Contains("[Error]", result.FullResponse);
        Assert.Contains("test error", result.FullResponse);
    }

    [Fact]
    public async Task EmptyTokens_NoFirstTokenCallback()
    {
        var source = new FakeStreamSource([]);
        var ui = CreateUiMock();
        ui.Setup(u => u.OnComplete());

        var processor = new ChatStreamProcessor(source, ui.Object);
        var result = await processor.ProcessAsync("test", null, CancellationToken.None);

        Assert.Equal("", result.FullResponse);
    }

    [Fact]
    public void HasUnclosedFence_OpeningOnly_ReturnsTrue()
    {
        Assert.True(ChatStreamProcessor.HasUnclosedFence("Some text ```code"));
    }

    [Fact]
    public void HasUnclosedFence_ClosedPair_ReturnsFalse()
    {
        Assert.False(ChatStreamProcessor.HasUnclosedFence("```code```"));
    }

    [Fact]
    public void HasUnclosedFence_NoFence_ReturnsFalse()
    {
        Assert.False(ChatStreamProcessor.HasUnclosedFence("just text"));
    }

    [Fact]
    public void HasUnclosedFence_MultiplePairs_ReturnsFalse()
    {
        Assert.False(ChatStreamProcessor.HasUnclosedFence("```a``` ```b```"));
    }
}
