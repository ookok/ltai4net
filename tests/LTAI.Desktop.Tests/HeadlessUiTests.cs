using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LTAI.Desktop.Tests;

[Collection("AvaloniaHeadless")]
public sealed class HeadlessUiSmokeTests : AvaloniaUITestBase
{
    [Fact]
    public void Button_Renders_WithText()
    {
        var btn = new Button { Content = "Hello", Width = 100, Height = 30 };
        var w = CreateWindow(btn);
        Assert.Equal("Hello", ((Button)w.Content!).Content);
        Assert.True(w.Width > 0);
        w.Close();
    }

    [Fact]
    public void StackPanel_Layout_Works()
    {
        var panel = new StackPanel
        {
            Spacing = 5,
            Children = { new TextBlock { Text = "Line 1", Height = 20 }, new TextBlock { Text = "Line 2", Height = 20 } }
        };
        var w = CreateWindow(panel);
        var stack = (StackPanel)w.Content!;
        Assert.Equal(2, stack.Children.Count);
        w.Close();
    }

    [Fact]
    public void LtaiTheme_Apply_DoesNotThrow()
    {
        var btn = new Button { Content = "Test" };
        btn.Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA);
        btn.Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent);
        var w = CreateWindow(btn);
        Assert.NotNull(btn.Background);
        w.Close();
    }

    [Fact]
    public void TextBox_Input_Works()
    {
        var tb = new TextBox { Width = 200 };
        var w = CreateWindow(tb);
        tb.Text = "test input";
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Normal);
        Assert.Equal("test input", tb.Text);
        w.Close();
    }

    [Fact]
    public void CheckBox_Toggle_Works()
    {
        var cb = new CheckBox { Content = "Option" };
        var w = CreateWindow(cb);
        Assert.False(cb.IsChecked == true);
        cb.IsChecked = true;
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Normal);
        Assert.True(cb.IsChecked == true);
        w.Close();
    }

    [Fact]
    public void ChatView_Constructor_DoesNotThrow()
    {
        var svc = new LTAIService(
            chat: new MockChatService(),
            options: Microsoft.Extensions.Options.Options.Create(new LTAI.Core.Configuration.LTAIOptions()));
        var ex = Record.Exception(() => new ChatView(svc));
        Assert.Null(ex);
    }

    private sealed class MockChatService : IChatService
    {
        public async IAsyncEnumerable<Microsoft.Agents.AI.AgentResponseUpdate> ChatStreamingAsync(
            string message, LTAI.Core.Session.ISessionHandle? sessionHandle = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        { yield break; }

        public Task<string> ChatAsync(string message, LTAI.Core.Session.ISessionHandle? sessionHandle = null, CancellationToken ct = default)
            => Task.FromResult("");
    }
}
