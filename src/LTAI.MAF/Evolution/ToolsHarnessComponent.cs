using LTAI.Core.Messaging;

namespace LTAI.MAF.Evolution;

public sealed class ToolsHarnessComponent : IHarnessComponent
{
    private readonly AIToolRegistry _registry;
    public string ComponentName => "tools";
    public string CurrentHash => ComputeHash(_registry.ListTools().Count().ToString());

    public ToolsHarnessComponent(AIToolRegistry registry) => _registry = registry;

    public Task<EvolutionFitness> EvaluateAsync(IServiceProvider sp, CancellationToken ct)
    {
        var total = _registry.ListTools().Count();
        var score = total >= 50 ? 1.0 : total / 50.0;
        return Task.FromResult(new EvolutionFitness { Score = score, Samples = total });
    }

    public Task ApplyEditAsync(HarnessEdit edit, IServiceProvider sp, CancellationToken ct)
        => Task.CompletedTask;

    public Task RollbackEditAsync(HarnessEdit edit, IServiceProvider sp, CancellationToken ct)
        => Task.CompletedTask;

    private static string ComputeHash(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
