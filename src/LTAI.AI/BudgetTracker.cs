using System.Collections.Generic;
using System.Text.Json;

namespace LTAI.AI;

public sealed class BudgetTracker
{
    private readonly long _globalMax;
    private readonly long _perUserMax;
    private readonly Dictionary<string, long> _userTokens = new(StringComparer.OrdinalIgnoreCase);
    private long _globalTotal;
    private readonly object _budgetLock = new();
    private readonly string? _persistPath;
    private int _saveCounter;

    public BudgetTracker(long globalMax = 1_000_000, long perUserMax = 200_000, string? persistPath = null)
    {
        _globalMax = globalMax;
        _perUserMax = perUserMax;
        _persistPath = persistPath;
        LoadState();
    }

    private void LoadState()
    {
        if (_persistPath == null || !File.Exists(_persistPath)) return;
        try
        {
            var json = File.ReadAllText(_persistPath);
            var state = JsonSerializer.Deserialize<PersistedState>(json);
            if (state != null)
            {
                foreach (var kv in state.Users)
                    _userTokens[kv.Key] = kv.Value;
                _globalTotal = state.GlobalTotal;
            }
        }
        catch { /* best-effort load */ }
    }

    private void SaveState()
    {
        if (_persistPath == null || Interlocked.Increment(ref _saveCounter) % 10 != 0) return;
        try
        {
            var state = new PersistedState { Users = new Dictionary<string, long>(_userTokens), GlobalTotal = _globalTotal };
            var tmp = _persistPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state));
            File.Move(tmp, _persistPath, overwrite: true);
        }
        catch { /* best-effort save */ }
    }

    private sealed class PersistedState
    {
        public Dictionary<string, long> Users { get; set; } = new();
        public long GlobalTotal { get; set; }
    }

    public (bool allowed, long remaining) TryConsume(string userId, int tokens)
    {
        if (tokens <= 0) return (true, _globalMax - _globalTotal);
        lock (_budgetLock)
        {
            _userTokens.TryGetValue(userId, out var currentUser);
            var newUser = currentUser + tokens;
            var newGlobal = _globalTotal + tokens;
            if (newUser > _perUserMax || newGlobal > _globalMax)
                return (false, Math.Min(_perUserMax - currentUser, _globalMax - _globalTotal));
            _userTokens[userId] = newUser;
            _globalTotal = newGlobal;
            SaveState();
            return (true, _globalMax - _globalTotal);
        }
    }

    public void Reset(string? userId = null)
    {
        lock (_budgetLock)
        {
            if (userId != null && _userTokens.Remove(userId, out var removed))
                _globalTotal -= removed;
            else if (userId == null)
            {
                _userTokens.Clear();
                _globalTotal = 0;
            }
        }
    }

    public long GetUserTotal(string userId) { lock (_budgetLock) return _userTokens.TryGetValue(userId, out var v) ? v : 0; }
    public long GlobalTotal { get { lock (_budgetLock) return _globalTotal; } }
}
