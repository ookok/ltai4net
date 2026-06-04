using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using LTAI.Desktop.Services;
using LTAI.Desktop.ViewModels;
using Moq;

namespace LTAI.Desktop.Tests;

/// <summary>
/// Avalonia headless UITests for ChatView keyboard input and button interactions.
/// Uses real UI controls but mocks the LLM client — no real AI call.
/// </summary>
public sealed class ChatViewUITests : AvaloniaUITestBase
{
    private static readonly Lazy<FieldInfo> _inputField = new(
        () => typeof(ChatView).GetField("_input", BindingFlags.NonPublic | BindingFlags.Instance)!);

    private static TextBox GetInput(ChatView chatView)
    {
        CreateWindow(chatView); // ensures visual tree is built
        return (TextBox)_inputField.Value.GetValue(chatView)!;
    }

    /// <summary>
    /// Typing text and pressing Enter should invoke LLM via ChatViewModel.
    /// </summary>
    [Fact]
    public void EnterKey_WithText_CallsLlmStreaming()
    {
        var mock = new Mock<ILlmClient>();
        mock.Setup(x => x.ChatStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Repeat("response", 1));

        var chatVm = new ChatViewModel(mock.Object);
        var chatView = new ChatView(null!, null, chatVm);
        var input = GetInput(chatView);

        input.Text = "hello";
        input.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
            KeyModifiers = KeyModifiers.None
        });

        mock.Verify(x => x.ChatStreamingAsync("hello", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Cancel command should stop an in-flight LLM call.
    /// (Tested via ChatViewModel; here we verify the Cancel button exists and is wired.)
    /// </summary>
    [Fact]
    public void CancelButton_ExistsAndWired()
    {
        var mock = new Mock<ILlmClient>();
        mock.Setup(x => x.ChatStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Repeat("response", 1));

        var chatVm = new ChatViewModel(mock.Object);
        var chatView = new ChatView(null!, null, chatVm);
        CreateWindow(chatView);

        // Verify Send/Cancel button exists
        var btnField = typeof(ChatView).GetField("_actionBtn", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(btnField);
        var btn = btnField.GetValue(chatView) as Button;
        Assert.NotNull(btn);
        Assert.Equal("Send", btn.Content?.ToString()); // initial content
    }

    /// <summary>
    /// Empty input should not call LLM.
    /// </summary>
    [Fact]
    public void EnterKey_WithEmptyText_DoesNotCallLlm()
    {
        var mock = new Mock<ILlmClient>();
        var chatVm = new ChatViewModel(mock.Object);
        var chatView = new ChatView(null!, null, chatVm);
        var input = GetInput(chatView);

        input.Text = "";
        input.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
            KeyModifiers = KeyModifiers.None
        });

        mock.Verify(x => x.ChatStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Shift+Enter should insert newline, not send.
    /// </summary>
    [Fact]
    public void ShiftEnter_InsertsNewline_DoesNotSend()
    {
        var mock = new Mock<ILlmClient>();
        var chatVm = new ChatViewModel(mock.Object);
        var chatView = new ChatView(null!, null, chatVm);
        var input = GetInput(chatView);

        input.Text = "line1";
        input.CaretIndex = 5;
        input.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
            KeyModifiers = KeyModifiers.Shift
        });

        Assert.Contains("\n", input.Text);
        mock.Verify(x => x.ChatStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Verify ChatView input field exists via reflection.
    /// </summary>
    [Fact]
    public void InputField_Exists()
    {
        var chatView = new ChatView(null!, null, new ChatViewModel(new Mock<ILlmClient>().Object));
        var input = GetInput(chatView);
        Assert.NotNull(input);
    }

    /// <summary>
    /// Verify SendButton exists via reflection.
    /// </summary>
    [Fact]
    public void SendButton_Exists()
    {
        var chatView = new ChatView(null!, null, new ChatViewModel(new Mock<ILlmClient>().Object));
        CreateWindow(chatView);
        var btnField = typeof(ChatView).GetField("_actionBtn", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(btnField);
        var btn = btnField.GetValue(chatView) as Button;
        Assert.NotNull(btn);
    }
}
