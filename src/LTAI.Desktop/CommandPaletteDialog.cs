using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LTAI.Desktop.ViewModels;

namespace LTAI.Desktop;

public sealed class CommandPaletteDialog : Window
{
    private readonly CommandPaletteViewModel _vm;
    private readonly TextBox _searchBox;
    private readonly ListBox _listBox;

    public CommandPaletteDialog(List<CommandPaletteViewModel.CommandPaletteItem> items)
    {
        _vm = new CommandPaletteViewModel(items);
        DataContext = _vm;
        Title = "命令面板";
        Width = 500;
        Height = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new DockPanel { Margin = new(8) };

        _searchBox = new TextBox
        {
            PlaceholderText = "搜索命令...",
            FontSize = 14,
            Margin = new(0, 0, 0, 8),
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
        };
        _searchBox.TextChanged += (_, _) => { _vm.Filter(_searchBox.Text ?? ""); _listBox.Items.Clear(); foreach (var item in _vm.FilteredItems) _listBox.Items.Add(new ListBoxItem { Content = new TextBlock { Text = $"{item.Icon}  {item.Title}  —  {item.Description}", FontSize = 12, Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) }, Tag = item }); };
        DockPanel.SetDock(_searchBox, Dock.Top);
        root.Children.Add(_searchBox);

        _listBox = new ListBox
        {
            Background = LtaiTheme.Sbb(LtaiTheme.Bg),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
        };

        foreach (var item in _vm.FilteredItems)
        {
            var textBlock = new TextBlock
            {
                Text = $"{item.Icon}  {item.Title}  —  {item.Description}",
                FontSize = 12,
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            };
            var listItem = new ListBoxItem { Content = textBlock, Tag = item };
            _listBox.Items.Add(listItem);
        }

        _listBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _listBox.SelectedItem is ListBoxItem selected && selected.Tag is CommandPaletteViewModel.CommandPaletteItem cmd)
            {
                cmd.Execute();
                Close();
            }
        };

        _listBox.PointerPressed += (_, e) =>
        {
            if (_listBox.SelectedItem is ListBoxItem selected && selected.Tag is CommandPaletteViewModel.CommandPaletteItem cmd)
            {
                cmd.Execute();
                Close();
            }
        };

        root.Children.Add(_listBox);
        Content = root;
    }
}
