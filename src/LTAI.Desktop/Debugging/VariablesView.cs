using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Layout;

namespace LTAI.Desktop.Debugging;

public sealed class VariablesView : TreeView
{
    private readonly DapSession _session;
    private readonly TextBox _watchBox;

    public VariablesView(DapSession session)
    {
        _session = session;
        Background = LtaiTheme.Sbb(LtaiTheme.BgPanel);
        Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary);
        MinHeight = 100;

        _session.StateChanged += _ => Refresh();
        _session.Stopped += (_, _) => Refresh();

        _watchBox = new TextBox
        {
            Watermark = "输入表达式求值 (Enter)",
            Background = LtaiTheme.Sbb(LtaiTheme.Bg),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontSize = 12,
        };
        _watchBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                var result = await _session.EvaluateAsync(_watchBox.Text ?? "");
                if (result != null)
                    AddWatchResult(_watchBox.Text!, result);
            }
        };
    }

    public Control CreateWithWatch()
    {
        var root = new DockPanel();
        DockPanel.SetDock(_watchBox, Dock.Top);
        root.Children.Add(_watchBox);
        root.Children.Add(this);
        return root;
    }

    public void Refresh()
    {
        Items.Clear();
        if (_session.State != DebugState.Paused || _session.CurrentScope.Length == 0)
        {
            Items.Add(new TreeViewItem
            {
                Header = _session.State == DebugState.Running ? "▶ Running..." : "⏹ Idle",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                IsHitTestVisible = false,
            });
            return;
        }

        foreach (var v in _session.CurrentScope)
            Items.Add(BuildVariableItem(v));
    }

    private TreeViewItem BuildVariableItem(DapVariable v)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var nameBlock = new TextBlock
        {
            Text = v.Name,
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            FontSize = 12,
            FontWeight = FontWeight.Bold,
        };
        header.Children.Add(nameBlock);

        var valueBlock = new TextBlock
        {
            Text = v.Value,
            Foreground = GetColorForValue(v.Value, v.Type),
            FontSize = 12,
        };
        header.Children.Add(valueBlock);

        if (!string.IsNullOrEmpty(v.Type))
        {
            header.Children.Add(new TextBlock
            {
                Text = $"({v.Type})",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontSize = 11,
            });
        }

        var item = new TreeViewItem { Header = header };

        if (v.VariablesReference > 0)
        {
            // Lazy-load children on expand
            item.Expanded += async (_, _) =>
            {
                if (item.Items.Count > 0) return;
                var children = await _session.ExpandVariableAsync(v.VariablesReference);
                foreach (var child in children)
                    item.Items.Add(BuildVariableItem(child));
            };
            // Add dummy child to show expand arrow
            item.Items.Add(new TreeViewItem { IsVisible = false });
        }

        return item;
    }

    private void AddWatchResult(string expression, string result)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock
        {
            Text = $"🔍 {expression}",
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            FontSize = 12,
        });
        header.Children.Add(new TextBlock
        {
            Text = "=",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontSize = 12,
        });
        header.Children.Add(new TextBlock
        {
            Text = result,
            Foreground = GetColorForValue(result, ""),
            FontSize = 12,
        });

        var item = new TreeViewItem { Header = header };
        Items.Insert(0, item);
    }

    private static IBrush GetColorForValue(string value, string type)
    {
        return type switch
        {
            "string" => LtaiTheme.Sbb(new Avalonia.Media.Color(255, 46, 204, 113)),
            "int" or "long" or "float" or "double" => LtaiTheme.Sbb(new Avalonia.Media.Color(255, 52, 152, 219)),
            "bool" => LtaiTheme.Sbb(new Avalonia.Media.Color(255, 155, 89, 182)),
            _ when value == "null" => LtaiTheme.Sbb(Avalonia.Media.Colors.Gray),
            _ => LtaiTheme.Sbb(LtaiTheme.TextPrimary),
        };
    }
}
