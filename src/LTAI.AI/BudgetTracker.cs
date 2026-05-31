using System.Collections.Concurrent;

namespace LTAI.AI;

/// <summary>
/// Token budget tracking with per-user isolation and configurable limits.
/// </summary>
public sealed class BudgetTracker
{
    private readonly long _globalMax;
    private readonly long _perUserMax;
    private readonly ConcurrentDictionary<string, long> _userTokens = new(StringComparer.OrdinalIgnoreCase);
    private long _globalTotal;
    private readonly object _budgetLock = new();

    public BudgetTracker(long globalMax = 1_000_000, long perUserMax = 200_000)
    {
        _globalMax = globalMax;
        _perUserMax = perUserMax;
    }

    public (bool allowed, long remaining) TryConsume(string userId, int tokens)
    {
        lock (_budgetLock)
        {
            _userTokens.TryGetValue(userId, out var currentUser);
            var newUser = currentUser + tokens;
            var newGlobal = _globalTotal + tokens;
            if (newUser > _perUserMax || newGlobal > _globalMax)
                return (false, Math.Min(_perUserMax - currentUser, _globalMax - _globalTotal));
            _userTokens[userId] = newUser;
            _globalTotal = newGlobal;
            return (true, _globalMax - _globalTotal);
        }
    }

    public void Reset(string? userId = null)
    {
        lock (_budgetLock)
        {
            if (userId != null && _userTokens.TryRemove(userId, out var removed))
                _globalTotal -= removed;
            else if (userId == null)
            {
                _userTokens.Clear();
                _globalTotal = 0;
            }
        }
    }

    public long GetUserTotal(string userId) { lock (_budgetLock) return _userTokens.GetValueOrDefault(userId); }
    public long GlobalTotal { get { lock (_budgetLock) return _globalTotal; } }
}
