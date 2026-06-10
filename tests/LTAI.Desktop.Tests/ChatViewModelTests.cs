using LTAI.Desktop.Services;
using LTAI.Desktop.ViewModels;
using Moq;

namespace LTAI.Desktop.Tests;

public sealed class ChatViewModelTests
{
    [Fact]
    public async Task SendCommand_SlashHelp_DoesNotCallLlm()
    {
        var mock = new Mock<ILlmClient>();
        var vm = new ChatViewModel(mock.Object);
        vm.Input = "/help";

        await vm.SendCommand.ExecuteAsync(null);

        // /help returns null StatusMessage (handled by ChatView rendering)
        Assert.Empty(vm.Messages);
        mock.Verify(c => c.ChatStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendCommand_SlashExit_FiresExitRequested()
    {
        var mock = new Mock<ILlmClient>();
        var vm = new ChatViewModel(mock.Object);
        vm.Input = "/exit";
        var exitFired = false;
        vm.ExitRequested += () => exitFired = true;

        await vm.SendCommand.ExecuteAsync(null);

        Assert.True(exitFired);
        mock.Verify(c => c.ChatStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendCommand_SlashNew_ClearsMessages()
    {
        var mock = new Mock<ILlmClient>();
        var vm = new ChatViewModel(mock.Object);
        vm.Input = "/new";
        await vm.SendCommand.ExecuteAsync(null);

        // /new creates a system message
        Assert.NotEmpty(vm.Messages);
        Assert.Equal("system", vm.Messages[0].Role);
        mock.Verify(c => c.ChatStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendCommand_NormalInput_CallsLlm()
    {
        var mock = new Mock<ILlmClient>();
        mock.Setup(c => c.ChatStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(EmptyAsyncEnumerable());
        var vm = new ChatViewModel(mock.Object);
        vm.Input = "hello";

        await vm.SendCommand.ExecuteAsync(null);

        mock.Verify(c => c.ChatStreamingAsync("hello", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendCommand_EmptyInput_DoesNothing()
    {
        var mock = new Mock<ILlmClient>();
        var vm = new ChatViewModel(mock.Object);
        vm.Input = "   ";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Empty(vm.Messages);
        mock.Verify(c => c.ChatStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendCommand_StreamingResponse_AddsAssistantMessage()
    {
        var mock = new Mock<ILlmClient>();
        mock.Setup(c => c.ChatStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(MakeStream("Hello", " ", "World"));
        var vm = new ChatViewModel(mock.Object);
        vm.Input = "hi";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Messages.Count);
        Assert.Equal("user", vm.Messages[0].Role);
        Assert.Equal("assistant", vm.Messages[1].Role);
        Assert.Equal("Hello World", vm.Messages[1].Text);
    }

    [Fact]
    public async Task SendCommand_SetsIsSending()
    {
        var mock = new Mock<ILlmClient>();
        var tcs = new TaskCompletionSource();
        mock.Setup(c => c.ChatStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableDelay(tcs.Task));
        var vm = new ChatViewModel(mock.Object);
        vm.Input = "hi";

        var sendTask = vm.SendCommand.ExecuteAsync(null);

        Assert.True(vm.IsSending);
        tcs.TrySetResult();
        await sendTask;
        Assert.False(vm.IsSending);
    }

    [Fact]
    public async Task SendCommand_LlmError_ShowsErrorMessage()
    {
        var mock = new Mock<ILlmClient>();
        mock.Setup(c => c.ChatStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("API key invalid"));
        var vm = new ChatViewModel(mock.Object);
        vm.Input = "hello";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Messages.Count);
        Assert.Equal("user", vm.Messages[0].Role);
        Assert.Equal("system", vm.Messages[1].Role);
        Assert.Contains("API key invalid", vm.Messages[1].Text);
    }

    [Fact]
    public async Task CancelCommand_StopsStreaming()
    {
        var mock = new Mock<ILlmClient>();
        mock.Setup(c => c.ChatStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(CancelOnCancelStream);
        var vm = new ChatViewModel(mock.Object);
        vm.Input = "hello";

        var sendTask = vm.SendCommand.ExecuteAsync(null);

        Assert.True(vm.IsSending);
        vm.CancelCommand.Execute(null);
        await sendTask;

        Assert.False(vm.IsSending);
        Assert.Contains(vm.Messages, m => m.Role == "assistant" && (m.Text?.Contains("已取消") ?? false));
    }

    /// <summary>Streams forever until cancellation is requested, then ends gracefully.</summary>
    private static async IAsyncEnumerable<string> CancelOnCancelStream(string _, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Yield();
            yield return "still...";
        }
    }

    private static async IAsyncEnumerable<string> AsyncEnumerableDelay(Task waitFor)
    {
        await waitFor;
        yield break;
    }

    private static async IAsyncEnumerable<string> MakeStream(params string[] tokens)
    {
        foreach (var t in tokens)
        {
            yield return t;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<string> EmptyAsyncEnumerable()
    {
        await Task.CompletedTask;
        yield break;
    }
}
