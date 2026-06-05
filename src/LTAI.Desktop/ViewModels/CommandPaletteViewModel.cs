using System.Collections.ObjectModel;

namespace LTAI.Desktop.ViewModels;

public sealed class CommandPaletteViewModel : ViewModelBase
{
    private readonly List<CommandPaletteItem> _allItems;

    public ObservableCollection<CommandPaletteItem> FilteredItems { get; } = new();

    public sealed record CommandPaletteItem(string Title, string Description, string Icon, Action Execute);

    public CommandPaletteViewModel(List<CommandPaletteItem> items)
    {
        _allItems = items;
        foreach (var item in items)
            FilteredItems.Add(item);
    }

    public void Filter(string? value)
    {
        FilteredItems.Clear();
        if (string.IsNullOrWhiteSpace(value))
        {
            foreach (var item in _allItems)
                FilteredItems.Add(item);
        }
        else
        {
            var q = value.ToLowerInvariant();
            foreach (var item in _allItems)
            {
                if (item.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    item.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
                    FilteredItems.Add(item);
            }
        }
    }

    public void Execute(CommandPaletteItem item) => item.Execute();
}
