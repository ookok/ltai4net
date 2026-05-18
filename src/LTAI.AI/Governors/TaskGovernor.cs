using LTAI.AI.Utilities;
using LTAI.Core.Execution;
using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class TaskGovernor : LayerGovernor
{
    private readonly TaskJournal _journal;
    private readonly Dictionary<string, List<string>> _dependencyMap = new();

    public TaskGovernor(ICognitiveMesh mesh, IChatClient llm, ILogger<TaskGovernor> logger, TaskJournal journal)
        : base("task", mesh, llm, logger)
    {
        _journal = journal;
    }

    public override Task<Handshake> ProcessAsync(Handshake incoming, CancellationToken cancellationToken = default)
    {
        var taskDescription = incoming.Payload?.GetValueOrDefault("task")?.ToString() ?? "";

        var subtasks = DecomposeTask(taskDescription);
        var entry = _journal.Add(taskDescription, new Dictionary<string, object?>
        {
            ["subtasks"] = subtasks
        });

        _journal.Complete(entry, "decomposed");

        return Task.FromResult(new Handshake
        {
            From = LayerName,
            Action = "task_decomposed",
            Payload = new Dictionary<string, object?>
            {
                ["task"] = taskDescription,
                ["subtasks"] = subtasks,
                ["count"] = subtasks.Length
            }
        });
    }

    private string[] DecomposeTask(string task) =>
        GovernorUtilities.DecomposeTask(task);

    public void AddDependency(string task, string dependsOn)
    {
        if (!_dependencyMap.ContainsKey(task))
            _dependencyMap[task] = new List<string>();
        _dependencyMap[task].Add(dependsOn);
    }
}
