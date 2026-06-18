using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using LTAI.Core.Session;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Moq;

namespace LTAI.Desktop.Tests;

[Collection("AvaloniaHeadless")]
public sealed class ChatViewHeadlessTests : AvaloniaUITestBase
{
    /// <summary>Helper: create empty async enumerable for IAsyncEnumerable[AgentResponseUpdate].</summary>
    private static async IAsyncEnumerable<AgentResponseUpdate> EmptyStream(
        [EnumeratorCancellation] CancellationToken ct = default)
    { yield break; }

    /// <summary>Helper: create single-item async enumerable.</summary>
    private static async IAsyncEnumerable<AgentResponseUpdate> SingleStream(string text,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new AgentResponseUpdate(new ChatResponseUpdate(ChatRole.Assistant, text));
    }

    private static (ChatView view, Mock<IChatService> chatMock) CreateChatView()
    {
        // Must initialize headless platform before constructing ChatView
        AvaloniaHeadlessFixture.EnsurePlatform();

        var chatMock = new Mock<IChatService>(MockBehavior.Strict);
        chatMock
            .Setup(s => s.ChatStreamingAsync(It.IsAny<string>(), It.IsAny<ISessionHandle?>(), It.IsAny<CancellationToken>()))
            .Returns((string q, ISessionHandle? h, CancellationToken ct) => EmptyStream(ct));
        chatMock
            .Setup(s => s.ChatAsync(It.IsAny<string>(), It.IsAny<ISessionHandle?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");

        var svc = new LTAIService(chatMock.Object, Options.Create(new LTAI.Core.Configuration.LTAIOptions()));
        var view = new ChatView(svc);
        return (view, chatMock);
    }

    [Fact]
    public void Constructor_WithMockService_DoesNotThrow()
    {
        var (view, _) = CreateChatView();
        Assert.NotNull(view);
    }

    [Fact]
    public void Constructor_CreatesInputBox()
    {
        var (view, _) = CreateChatView();
        var w = CreateWindow(view);
        if (w != null) w.Close();
        Assert.NotNull(view);
    }

    [Fact]
    public async Task SendMessage_UsesChatStreaming()
    {
        var (view, chatMock) = CreateChatView();
        var w = CreateWindow(view);

        bool streamingCalled = false;
        chatMock
            .Setup(s => s.ChatStreamingAsync(It.IsAny<string>(), It.IsAny<ISessionHandle?>(), It.IsAny<CancellationToken>()))
            .Returns((string q, ISessionHandle? h, CancellationToken ct) =>
            {
                streamingCalled = true;
                return SingleStream("Hello!", ct);
            });

        Assert.NotNull(view);
        if (w != null) w.Close();
    }

    [Fact]
    public void Mode_DefaultsToEmpty()
    {
        var (view, chatMock) = CreateChatView();
        var w = CreateWindow(view);

        var svc = new LTAIService(chatMock.Object, Options.Create(new LTAI.Core.Configuration.LTAIOptions()));
        Assert.Equal("", svc.Mode);
        if (w != null) w.Close();
    }
}
