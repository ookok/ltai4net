using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LTAI.Desktop;

public sealed class ChatView : UserControl
{
    private readonly LTAIService _svc;
    private readonly TextBox _input;
    private readonly TextBlock _output;
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _stats;
    private CancellationTokenSource? _cts;
    private int _turns, _tokens;

    public ChatView(LTAIService svc)
    {
        _svc = svc;
        Background = new SolidColorBrush(Color.Parse("#0d1117"));

        var root = new DockPanel { Margin = new(16) };

        _stats = new TextBlock { Text = "Turns: 0 | Tokens: 0", Foreground = new SolidColorBrush(Color.Parse("#484f58")), FontSize = 11 };
        DockPanel.SetDock(_stats, Dock.Top);
        root.Children.Add(_stats);

        var inputBar = new DockPanel { Margin = new(0, 8) };
        _input = new TextBox { Watermark = "Type here... (Enter to send)", Foreground = new SolidColorBrush(Color.Parse("#c9d1d9")), Background = new SolidColorBrush(Color.Parse("#161b22")), FontFamily = new("Consolas"), MinHeight = 32 };
        _input.KeyDown += OnInputKey;
        var sendBtn = new Button { Content = "Send", Width = 60 };
        sendBtn.Click += (_, _) => _ = SendAsync();
        DockPanel.SetDock(inputBar, Dock.Bottom);
        inputBar.Children.Add(sendBtn); inputBar.Children.Add(_input);
        DockPanel.SetDock(sendBtn, Dock.Right);
        DockPanel.SetDock(inputBar, Dock.Bottom);
        root.Children.Add(inputBar);

        _output = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#c9d1d9")), FontFamily = new("Consolas"), FontSize = 13 };
        _scroller = new ScrollViewer { Content = _output };
        root.Children.Add(_scroller);

        Content = root;
    }

    private async void OnInputKey(object? s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(_input.Text))
            await SendAsync();
    }

    private async Task SendAsync()
    {
        var query = _input.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;
        _input.Text = "";
        _turns++; UpdateStats();

        _output.Text += $"\n\n[You] {query}";
        _output.Text += "\n\n[LTAI] ";

        _cts = new CancellationTokenSource();
        try
        {
            var sb = new StringBuilder();
            await foreach (var token in _svc.LTS.StreamChatAsync(query).WithCancellation(_cts.Token))
            {
                sb.Append(token);
                _tokens++;
                _output.Text += token;
                _scroller.ScrollToEnd();
            }
            _output.Text += "\n";
            UpdateStats();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _output.Text += $"\n[Error] {ex.Message}"; }
        finally { _cts?.Dispose(); _cts = null; }
    }

    private void UpdateStats() =>
        _stats.Text = $"Turns: {_turns} | Tokens: {_tokens} | Model: {_svc.LTS.Mode}";
}
