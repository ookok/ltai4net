namespace LTAI.TUI;

/// <summary>Ring buffer for input history with navigation support.</summary>
public sealed class InputHistory
{
    private readonly List<string> _items = new();
    private int _index = -1;
    private readonly int _maxCapacity;

    public int Count => _items.Count;
    public int Index => _index;

    public InputHistory(int maxCapacity = 100)
    {
        _maxCapacity = Math.Max(1, maxCapacity);
    }

    public void Add(string item)
    {
        if (string.IsNullOrEmpty(item)) return;
        if (_items.Count > 0 && _items[^1] == item) return;
        _items.Add(item);
        if (_items.Count > _maxCapacity)
            _items.RemoveAt(0);
        _index = _items.Count;
    }

    public string? Previous()
    {
        if (_items.Count == 0) return null;
        _index = Math.Max(0, _index - 1);
        return _items[_index];
    }

    public string? Next()
    {
        if (_items.Count == 0) return null;
        _index = Math.Min(_items.Count, _index + 1);
        return _index < _items.Count ? _items[_index] : null;
    }

    public void ResetIndex()
    {
        _index = _items.Count;
    }

    public IReadOnlyList<string> Items => _items;
}
