namespace LTAI.Agent.Memory;

/// <summary>
/// Token budgets for each memory palace layer.
/// Supports dynamic adjustment based on context window size.
/// </summary>
public static class MemoryBudget
{
    // Default budgets (for 64K context window)
    private const int DefaultL0MaxTokens = 100;
    private const int DefaultL1MaxTokens = 800;
    private const int DefaultL3MaxTokens = 500;
    private const int DefaultL4MaxTokens = 2000;
    private const int DefaultL5MaxTokens = 400;
    private const int DefaultL6MaxTokens = 200;

    // Current budgets (may be adjusted dynamically)
    private static int _l0MaxTokens = DefaultL0MaxTokens;
    private static int _l1MaxTokens = DefaultL1MaxTokens;
    private static int _l3MaxTokens = DefaultL3MaxTokens;
    private static int _l4MaxTokens = DefaultL4MaxTokens;
    private static int _l5MaxTokens = DefaultL5MaxTokens;
    private static int _l6MaxTokens = DefaultL6MaxTokens;

    private static int _contextWindowSize = 64000;
    private static readonly object _lock = new();

    public static int L0MaxTokens { get { lock (_lock) return _l0MaxTokens; } }
    public static int L1MaxTokens { get { lock (_lock) return _l1MaxTokens; } }
    public static int L3MaxTokens { get { lock (_lock) return _l3MaxTokens; } }
    public static int L4MaxTokens { get { lock (_lock) return _l4MaxTokens; } }
    public static int L5MaxTokens { get { lock (_lock) return _l5MaxTokens; } }
    public static int L6MaxTokens { get { lock (_lock) return _l6MaxTokens; } }

    public static int TotalFixedTokens
    {
        get { lock (_lock) return _l0MaxTokens + _l1MaxTokens; }
    }

    public static int TotalMemoryTokens
    {
        get { lock (_lock) return _l0MaxTokens + _l1MaxTokens + _l3MaxTokens + _l4MaxTokens + _l6MaxTokens; }
    }

    /// <summary>
    /// Adjust budgets based on context window size.
    /// Larger windows get proportionally more memory budget.
    /// </summary>
    public static void AdjustForContextWindow(int contextWindowSize)
    {
        if (contextWindowSize <= 0) return;

        lock (_lock)
        {
            _contextWindowSize = contextWindowSize;

            // Scale factor: 1.0 for 64K, 2.0 for 128K, 0.5 for 32K
            var scale = Math.Clamp(contextWindowSize / 64000.0, 0.25, 4.0);

            _l0MaxTokens = (int)(DefaultL0MaxTokens * scale);
            _l1MaxTokens = (int)(DefaultL1MaxTokens * scale);
            _l3MaxTokens = (int)(DefaultL3MaxTokens * scale);
            _l4MaxTokens = (int)(DefaultL4MaxTokens * scale);
            _l5MaxTokens = (int)(DefaultL5MaxTokens * scale);
            _l6MaxTokens = (int)(DefaultL6MaxTokens * scale);
        }
    }

    /// <summary>
    /// Get current memory usage as percentage of context window.
    /// </summary>
    public static double GetMemoryUsageRatio()
    {
        lock (_lock)
        {
            if (_contextWindowSize <= 0) return 0;
            return (double)TotalMemoryTokens / _contextWindowSize;
        }
    }

    /// <summary>
    /// Check if adding more tokens would exceed the budget.
    /// </summary>
    public static bool WouldExceedBudget(int additionalTokens)
    {
        lock (_lock)
        {
            // Reserve 50% for conversation, 30% for tools, 20% for memory
            var memoryBudget = _contextWindowSize * 0.2;
            return (TotalMemoryTokens + additionalTokens) > memoryBudget;
        }
    }

    /// <summary>
    /// Reset budgets to defaults.
    /// </summary>
    public static void ResetToDefaults()
    {
        lock (_lock)
        {
            _l0MaxTokens = DefaultL0MaxTokens;
            _l1MaxTokens = DefaultL1MaxTokens;
            _l3MaxTokens = DefaultL3MaxTokens;
            _l4MaxTokens = DefaultL4MaxTokens;
            _l5MaxTokens = DefaultL5MaxTokens;
            _l6MaxTokens = DefaultL6MaxTokens;
        }
    }
}
