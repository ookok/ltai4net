namespace LTAI.Agent.Memory;

/// <summary>
/// Token budgets for each memory palace layer.
/// Referenced by all L0-L6 providers instead of duplicated const ints.
/// </summary>
public static class MemoryBudget
{
    public const int L0MaxTokens = 100;
    public const int L1MaxTokens = 800;
    public const int L3MaxTokens = 500;
    public const int L4MaxTokens = 2000;
    public const int L5MaxTokens = 400;
    public const int L6MaxTokens = 200;

    public static int TotalFixedTokens => L0MaxTokens + L1MaxTokens;
}
