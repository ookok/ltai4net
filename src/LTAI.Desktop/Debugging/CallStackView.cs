using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Layout;
using LTAI.Core.Debugging;

namespace LTAI.Desktop.Debugging;

public sealed class CallStackView : TreeView
{
    private readonly DapSession _session;
    public event Action<string, int>? NavigateToFile;

    public CallStackView(DapSession session)
    {
        _session = session;
        Background = LtaiTheme.Sbb(LtaiTheme.BgPanel);
        Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary);
        MinHeight = 100;
        _session.StateChanged += _ => Refresh();
        _session.Stopped += (_, _) => Refresh();
    }

    public void Refresh()
    {
        Items.Clear();
        if (_session.State != LTAI.Core.Debugging.DebugState.Paused || _session.CurrentStack.Length == 0)
        {
            Items.Add(new TreeViewItem
            {
                Header = _session.State == LTAI.Core.Debugging.DebugState.Running ? "▶ Running..." : "⏹ Idle",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                IsHitTestVisible = false,
            });
            return;
        }

        foreach (var frame in _session.CurrentStack)
        {
            var header = $"{frame.Name}";
            var detail = frame.File != null
                ? $"{Path.GetFileName(frame.File)}:{frame.Line}"
                : "[native/unknown]";

            var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            stack.Children.Add(new TextBlock
            {
                Text = "▸",
                Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
                FontSize = 12,
            });
            stack.Children.Add(new TextBlock
            {
                Text = header,
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                FontSize = 12,
            });
            stack.Children.Add(new TextBlock
            {
                Text = detail,
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontSize = 11,
            });

            var item = new TreeViewItem
            {
                Header = stack,
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            };

            item.PointerPressed += (_, e) =>
            {
                if (frame.File != null && frame.Line > 0)
                    NavigateToFile?.Invoke(frame.File, frame.Line);
            };

            Items.Add(item);
        }
    }
}
