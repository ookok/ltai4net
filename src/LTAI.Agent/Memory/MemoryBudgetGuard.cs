namespace LTAI.Agent.Memory;

public sealed class MemoryBudgetGuard
{
    public int L0MaxTokens { get; set; } = 100;
    public int L1MaxTokens { get; set; } = 800;
    public int L3MaxTokens { get; set; } = 500;
    public int L4MaxTokens { get; set; } = 2000;
    public int L5MaxTokens { get; set; } = 400;
    public int L6MaxTokens { get; set; } = 200;

    public int TotalFixedTokens => L0MaxTokens + L1MaxTokens;

    public override string ToString() =>
        $"L0={L0MaxTokens} L1={L1MaxTokens} L3={L3MaxTokens} L4={L4MaxTokens} L5={L5MaxTokens} L6={L6MaxTokens} = {TotalFixedTokens}+ fixed";
}
