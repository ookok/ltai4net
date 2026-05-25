using LTAI.Core.Messaging;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Evolution;

public sealed class ToolsHarnessComponent : IHarnessComponent
{
    private readonly AIToolRegistry _registry;
    private readonly ILogger<ToolsHarnessComponent>? _logger;
    private readonly string _editLogDir;
    public string ComponentName => "tools";
    public string CurrentHash => ComputeHash(_registry.ListTools().Count().ToString());

    public ToolsHarnessComponent(AIToolRegistry registry, ILogger<ToolsHarnessComponent>? logger = null)
    {
        _registry = registry;
        _logger = logger;
        _editLogDir = global::System.IO.Path.Combine(".livingtree", "harness_edits");
        global::System.IO.Directory.CreateDirectory(_editLogDir);
    }

    public Task<EvolutionFitness> EvaluateAsync(IServiceProvider sp, CancellationToken ct)
    {
        var total = _registry.ListTools().Count();
        var score = total >= 50 ? 1.0 : total / 50.0;
        return Task.FromResult(new EvolutionFitness { Score = score, Samples = total });
    }

    public async Task ApplyEditAsync(HarnessEdit edit, IServiceProvider sp, CancellationToken ct)
    {
        var logPath = global::System.IO.Path.Combine(_editLogDir, $"{edit.Id}.json");
        var snapshot = new
        {
            edit.Id, edit.Component, edit.RootCause, edit.Fix,
            applied_at = DateTime.UtcNow.ToString("O"),
            tool_count_before = _registry.ListTools().Count()
        };
        await global::System.IO.File.WriteAllTextAsync(logPath,
            global::System.Text.Json.JsonSerializer.Serialize(snapshot), ct).ConfigureAwait(false);

        _logger?.LogInformation("Harness edit applied: {EditId} - {Fix}",
            edit.Id, edit.Fix);
    }

    public async Task RollbackEditAsync(HarnessEdit edit, IServiceProvider sp, CancellationToken ct)
    {
        var logPath = global::System.IO.Path.Combine(_editLogDir, $"{edit.Id}.json");
        if (global::System.IO.File.Exists(logPath))
        {
            var rollbackLog = global::System.IO.Path.Combine(_editLogDir, $"{edit.Id}.rollback.json");
            global::System.IO.File.Move(logPath, rollbackLog);
            _logger?.LogInformation("Harness edit rolled back: {EditId}", edit.Id);
        }
    }

    private static string ComputeHash(string input)
    {
        var bytes = global::System.Security.Cryptography.SHA256.HashData(
            global::System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
