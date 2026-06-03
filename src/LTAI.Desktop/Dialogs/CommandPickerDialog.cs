using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace LTAI.Desktop.Dialogs;

public sealed class CommandPickerDialog : Window
{
    private readonly ListBox _list;
    public string? Selected { get; private set; }

    public CommandPickerDialog(string title, string[] items)
    {
        Title = title;
        Width = 460;
        Height = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Topmost = true;

        _list = new ListBox
        {
            ItemsSource = items.ToList(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is string s) Selected = s.Split(' ')[0].TrimEnd('…');
        };
        _list.DoubleTapped += (_, _) => { if (_list.SelectedItem != null) Close(); };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _list.SelectedItem != null) Close();
            if (e.Key == Key.Escape) { Selected = null; Close(); }
        };

        Content = _list;
    }
}
