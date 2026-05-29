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
        // Check before adding — prevents overdraft from large bursts
        var currentUser = _userTokens.GetValueOrDefault(userId);
        var currentGlobal = Interlocked.Read(ref _globalTotal);
        var newUser = currentUser + tokens;
        var newGlobal = currentGlobal + tokens;

        if (newUser > _perUserMax || newGlobal > _globalMax)
            return (false, Math.Min(_perUserMax - currentUser, _globalMax - currentGlobal));

        // Budget OK — atomically add
        _userTokens.AddOrUpdate(userId, tokens, (_, old) => old + tokens);
        Interlocked.Add(ref _globalTotal, tokens);
        return (true, Math.Min(_perUserMax - newUser, _globalMax - newGlobal));
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
