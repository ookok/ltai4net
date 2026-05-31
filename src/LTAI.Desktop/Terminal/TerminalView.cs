using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LTAI.Desktop.Terminal;

/// <summary>Avalonia 终端控件 — 嵌入可交互的 ConPTY 命令行。</summary>
public sealed class TerminalView : UserControl
{
    private readonly VirtualTerminal _terminal = new();
    private readonly TextBlock _output;
    private readonly TextBox _input;
    private readonly StackPanel _root;
    private string _inputBuf = "";

    public string WorkingDirectory { get; set; } = Environment.CurrentDirectory;

    public TerminalView()
    {
        _output = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            Background = LtaiTheme.Sbb(Color.Parse("#0d1117")),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 200,
        };

        _input = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Background = LtaiTheme.Sbb(Color.Parse("#161b22")),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            Height = 22,
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                var cmd = _input.Text?.Trim();
                if (!string.IsNullOrEmpty(cmd))
                {
                    _terminal.WriteInput(cmd + "\r\n");
                    _inputBuf += $"> {cmd}\n";
                    _input.Text = "";
                    RefreshOutput();
                }
                e.Handled = true;
            }
        };

        var header = new TextBlock
        {
            Text = "📟 终端 (ConPTY)",
            FontSize = 11,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
        };

        _root = new StackPanel
        {
            Background = LtaiTheme.Sbb(Color.Parse("#0d1117")),
            Children = { header, new ScrollViewer { Content = _output, MaxHeight = 200 }, _input }
        };
        Content = _root;

        _terminal.OutputUpdated += () => Dispatcher.UIThread.Post(RefreshOutput);
    }

    public void Start() => _terminal.Start(workingDir: WorkingDirectory);

    public void Stop() => _terminal.Stop();

    private void RefreshOutput()
    {
        var lines = _terminal.Screen;
        var last = lines.Count > 0 ? string.Join("\n", lines.TakeLast(30)) : "";
        _output.Text = _inputBuf + last;
    }
}
