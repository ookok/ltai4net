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

    public BudgetTracker(long globalMax = 1_000_000, long perUserMax = 200_000)
    {
        _globalMax = globalMax;
        _perUserMax = perUserMax;
    }

    public (bool allowed, long remaining) TryConsume(string userId, int tokens)
    {
        var userTotal = _userTokens.AddOrUpdate(userId, tokens, (_, old) => old + tokens);
        var global = Interlocked.Add(ref _globalTotal, tokens);
        return (userTotal <= _perUserMax && global <= _globalMax,
                Math.Min(_perUserMax - userTotal, _globalMax - global));
    }

    public void Reset(string? userId = null)
    {
        if (userId != null)
            _userTokens.TryRemove(userId, out _);
        else
        {
            _userTokens.Clear();
            Interlocked.Exchange(ref _globalTotal, 0);
        }
    }

    public long GetUserTotal(string userId) => _userTokens.GetValueOrDefault(userId);
    public long GlobalTotal => Interlocked.Read(ref _globalTotal);
}
