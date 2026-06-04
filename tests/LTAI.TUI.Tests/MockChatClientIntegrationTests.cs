using System.Runtime.CompilerServices;
using LTAI.TUI.Commands;
using Microsoft.Extensions.AI;
using Moq;

namespace LTAI.TUI.Tests;

public sealed class MockChatClientIntegrationTests
{
    private static string ExtractPrompt(IEnumerable<ChatMessage> messages)
    {
        var texts = messages.Select(m => m.Text).Where(t => t != null);
        return string.Join(" ", texts).Trim();
    }

    // ── Moq static-like: exact prompt → fixed response ──

    [Fact]
    public async Task Moq_ExactPromptMatch_ReturnsFixedResponse()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(x => x.GetResponseAsync(
                It.Is<IEnumerable<ChatMessage>>(msgs => ExtractPrompt(msgs) == "what is AI"),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "AI stands for Artificial Intelligence.")));

        var response = await mock.Object.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "what is AI") });

        Assert.Equal("AI stands for Artificial Intelligence.", response.Text);
    }

    [Fact]
    public async Task Moq_MultiMessagePrompt_MergesToKey()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(x => x.GetResponseAsync(
                It.Is<IEnumerable<ChatMessage>>(msgs => ExtractPrompt(msgs) == "hello world"),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hi there!")));

        var response = await mock.Object.GetResponseAsync(new List<ChatMessage>
        {
            new(ChatRole.User, "hello"),
            new(ChatRole.User, "world"),
        });

        Assert.Equal("Hi there!", response.Text);
    }

    [Fact]
    public async Task Moq_UnknownPrompt_ReturnsNull()
    {
        var mock = new Mock<IChatClient>(MockBehavior.Strict);
        mock.Setup(x => x.GetResponseAsync(
                It.Is<IEnumerable<ChatMessage>>(msgs => ExtractPrompt(msgs) == "known"),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        // Moq strict: no setup for "unknown" → throws
        await Assert.ThrowsAsync<Moq.MockException>(() =>
            mock.Object.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, "unknown") }));
    }

    [Fact]
    public async Task Moq_PatternMatch_WithPredicate()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(x => x.GetResponseAsync(
                It.Is<IEnumerable<ChatMessage>>(msgs => ExtractPrompt(msgs).Contains("capital of France", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Paris")));

        var response = await mock.Object.GetResponseAsync(new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant."),
            new(ChatRole.User, "What is the capital of France?"),
        });

        Assert.Equal("Paris", response.Text);
    }

    // ── Sequential responses with Moq SetupSequence ──

    [Fact]
    public async Task Moq_SetupSequence_ReturnsSequentialResponses()
    {
        var mock = new Mock<IChatClient>();
        mock.SetupSequence(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "first")))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "second")))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "third")));

        var msg = new List<ChatMessage> { new(ChatRole.User, "irrelevant") };

        Assert.Equal("first", (await mock.Object.GetResponseAsync(msg)).Text);
        Assert.Equal("second", (await mock.Object.GetResponseAsync(msg)).Text);
        Assert.Equal("third", (await mock.Object.GetResponseAsync(msg)).Text);
    }

    [Fact]
    public async Task Moq_Strict_UnknownCall_ThrowsMockException()
    {
        var mock = new Mock<IChatClient>(MockBehavior.Strict);
        mock.Setup(x => x.GetResponseAsync(
                It.Is<IEnumerable<ChatMessage>>(msgs => ExtractPrompt(msgs) == "known"),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        await mock.Object.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, "known") });

        // Strict: "unknown" has no matching setup → MockException
        await Assert.ThrowsAsync<Moq.MockException>(() =>
            mock.Object.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, "unknown") }));
    }

    // ── Streaming ──

    private static IAsyncEnumerable<ChatResponseUpdate> StreamFrom(string text)
    {
        return Core(text);
        static async IAsyncEnumerable<ChatResponseUpdate> Core(string t)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, t);
        }
    }

    [Fact]
    public async Task Moq_Streaming_YieldsSingleChunk()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(x => x.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(StreamFrom("world"));

        var chunks = await mock.Object.GetStreamingResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, "hello") }).ToListAsync();

        Assert.Single(chunks);
        Assert.Equal("world", chunks[0].Text);
    }

    // ── Moq Verify — call count and argument inspection ──

    [Fact]
    public async Task Moq_Verify_CalledOnce()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        await mock.Object.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, "hi") });

        mock.Verify(x => x.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Moq_Verify_WithArgumentPredicate()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        await mock.Object.GetResponseAsync(new List<ChatMessage> { new(ChatRole.User, "hello world") });

        mock.Verify(x => x.GetResponseAsync(
            It.Is<IEnumerable<ChatMessage>>(msgs => ExtractPrompt(msgs) == "hello world"),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Moq_Verify_NotCalled()
    {
        var mock = new Mock<IChatClient>();

        // Don't call the mock at all
        mock.Verify(x => x.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Moq_Callback_InspectsInputMessages()
    {
        IEnumerable<ChatMessage>? captured = null;
        var mock = new Mock<IChatClient>();
        mock.Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, _, _) => captured = msgs.ToList())
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        await mock.Object.GetResponseAsync(new List<ChatMessage>
        {
            new(ChatRole.System, "sys"),
            new(ChatRole.User, "hello"),
        });

        Assert.NotNull(captured);
        Assert.Contains(captured, m => m.Role == ChatRole.System);
        Assert.Contains(captured, m => m.Text == "hello");
    }

    // ── Full chain: CommandParser → SlashCommands → Mock ──

    [Fact]
    public void FullChain_ParserThenExecute_DispatchWorks()
    {
        var parser = new CommandParser();
        string? status = null;
        var running = true;

        var cmd = parser.Parse("/help");
        var handled = SlashCommands.TryExecute("/help", ref running, ref status);

        Assert.True(handled);
        Assert.NotNull(status);
        Assert.Contains("help", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FullChain_NormalMessage_PassesThroughToLLM()
    {
        string? status = null;
        var running = true;

        var handled = SlashCommands.TryExecute("what is AI", ref running, ref status);

        Assert.False(handled);
    }

    [Fact]
    public async Task FullChain_CommandParserThenMockLLM_Deterministic()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(x => x.GetResponseAsync(
                It.Is<IEnumerable<ChatMessage>>(msgs => ExtractPrompt(msgs) == "what is AI"),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "AI stands for Artificial Intelligence.")));

        var parser = new CommandParser();
        var input = "what is AI";
        var cmd = parser.Parse(input);

        Assert.IsType<ChatMessageCommand>(cmd);

        var response = await mock.Object.GetResponseAsync(
            new List<ChatMessage> { new(ChatRole.User, input) });

        Assert.Equal("AI stands for Artificial Intelligence.", response.Text);
    }

    [Fact]
    public async Task FullChain_RouteByCommandType_MockResponds()
    {
        var mock = new Mock<IChatClient>();
        mock.Setup(x => x.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "mock response")));

        var parser = new CommandParser();
        var inputs = new[] { "/ask search kb", "/new", "/ask summarize" };

        foreach (var input in inputs)
        {
            var cmd = parser.Parse(input);

            if (cmd is ChatMessageCommand)
            {
                var response = await mock.Object.GetResponseAsync(
                    new List<ChatMessage> { new(ChatRole.User, input) });
                Assert.Equal("mock response", response.Text);
            }
        }
    }

    [Fact]
    public async Task FullChain_FuzzyCommand_NotSentToLLM()
    {
        var mock = new Mock<IChatClient>(MockBehavior.Strict);
        string? status = null;
        var running = true;

        var handled = SlashCommands.TryExecute("/sttus", ref running, ref status);

        Assert.True(handled);
        Assert.Contains("status", status, StringComparison.OrdinalIgnoreCase);

        // LLM was never called (fuzzy match caught by command parser)
        mock.Verify(x => x.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FullChain_UnknownCommand_ShowsHelpNotLLM()
    {
        var mock = new Mock<IChatClient>(MockBehavior.Strict);
        string? status = null;
        var running = true;

        var handled = SlashCommands.TryExecute("/xyzzy", ref running, ref status);

        Assert.True(handled);
        Assert.Contains("help", status, StringComparison.OrdinalIgnoreCase);

        // LLM never called
        mock.Verify(x => x.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
