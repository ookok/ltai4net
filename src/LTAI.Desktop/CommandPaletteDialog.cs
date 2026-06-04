using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LTAI.Desktop;

public sealed record CommandPaletteItem(string Title, string Description, string Icon, Action Execute);

public sealed class CommandPaletteDialog : Window
{
    private readonly TextBox _searchBox;
    private readonly ListBox _listBox;
    private readonly List<CommandPaletteItem> _allItems;

    public CommandPaletteDialog(List<CommandPaletteItem> items)
    {
        _allItems = items;
        Title = "Command Palette";
        Width = 480;
        Height = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);
        CanResize = false;

        _searchBox = new TextBox
        {
            PlaceholderText = "输入命令名称...",
            Margin = new(8, 8, 8, 0),
            Height = 28,
            FontSize = 13,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            BorderThickness = new(1)
        };

        _listBox = new ListBox
        {
            Margin = new(8, 8, 8, 8),
            MaxHeight = 240,
            Background = LtaiTheme.Sbb(Colors.Transparent)
        };
        _listBox.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<CommandPaletteItem>((item, _) =>
        {
            var dock = new DockPanel { Margin = new(4, 2) };
            dock.Children.Add(new TextBlock
            {
                Text = item.Icon,
                Width = 24,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14
            });
            dock.Children.Add(new TextBlock
            {
                Text = item.Title,
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });
            dock.Children.Add(new TextBlock
            {
                Text = item.Description,
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new(8, 0, 4, 0)
            });
            DockPanel.SetDock(dock.Children[^1], Dock.Right);
            return dock;
        });

        _searchBox.TextChanged += (_, _) => Filter();
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down)
            {
                if (_listBox.SelectedIndex < _listBox.ItemCount - 1)
                    _listBox.SelectedIndex++;
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                if (_listBox.SelectedIndex > 0)
                    _listBox.SelectedIndex--;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (_listBox.SelectedItem is CommandPaletteItem sel)
                { sel.Execute(); Close(); }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };

        _listBox.DoubleTapped += (_, _) =>
        {
            if (_listBox.SelectedItem is CommandPaletteItem sel)
            { sel.Execute(); Close(); }
        };

        var root = new StackPanel();
        root.Children.Add(_searchBox);
        root.Children.Add(_listBox);
        Content = root;

        Filter();

        Opened += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => _searchBox.Focus());
            _listBox.SelectedIndex = 0;
        };
    }

    private void Filter()
    {
        var q = _searchBox.Text?.Trim().ToLowerInvariant() ?? "";
        var filtered = string.IsNullOrEmpty(q)
            ? _allItems
            : _allItems.Where(i => i.Title.ToLowerInvariant().Contains(q)
                                || i.Description.ToLowerInvariant().Contains(q)).ToList();
        _listBox.ItemsSource = filtered;
        if (filtered.Count > 0) _listBox.SelectedIndex = 0;
    }
}
