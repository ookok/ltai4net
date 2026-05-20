namespace LTAI.MAF.Workflows;

public enum WorkflowStepStatus { Pending, Running, Done, Failed, Skipped }

public sealed class WorkflowStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string Input { get; set; } = "";
    public string Output { get; set; } = "";
    public WorkflowStepStatus Status { get; set; } = WorkflowStepStatus.Pending;
    public string Error { get; set; } = "";
    public long LatencyMs { get; set; }
    public List<string> DependsOn { get; set; } = new();
}

public sealed class WorkflowResult
{
    public string WorkflowId { get; set; } = "";
    public string Status { get; set; } = "completed";
    public List<WorkflowStep> Steps { get; set; } = new();
    public long TotalLatencyMs { get; set; }
}

public sealed class ConcurrentWorkflow
{
    private readonly Func<string, string, Task<string>> _agentFn;
    private readonly int _maxParallel;

    public ConcurrentWorkflow(Func<string, string, Task<string>> agentFn, int maxParallel = 4)
    {
        _agentFn = agentFn;
        _maxParallel = maxParallel;
    }

    public async Task<WorkflowResult> RunAsync(List<(string agent, string task)> items)
    {
        var result = new WorkflowResult { WorkflowId = Guid.NewGuid().ToString("N")[..8] };
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        var steps = new List<WorkflowStep>();

        using var semaphore = new SemaphoreSlim(_maxParallel);
        var tasks = items.Select(async item =>
        {
            await semaphore.WaitAsync();
            try
            {
                var step = new WorkflowStep { Name = item.task, AgentName = item.agent, Input = item.task, Status = WorkflowStepStatus.Running };
                var t0 = global::System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    step.Output = await _agentFn(item.agent, item.task);
                    step.Status = WorkflowStepStatus.Done;
                }
                catch (Exception ex)
                {
                    step.Status = WorkflowStepStatus.Failed;
                    step.Error = ex.Message;
                }
                step.LatencyMs = t0.ElapsedMilliseconds;
                lock (steps) { steps.Add(step); }
            }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(tasks);
        result.Steps = steps.OrderBy(s => steps.IndexOf(s)).ToList();
        result.TotalLatencyMs = sw.ElapsedMilliseconds;
        result.Status = steps.All(s => s.Status == WorkflowStepStatus.Done) ? "completed" : "partial";
        return result;
    }
}

public sealed class SimpleHandoff
{
    public async Task<string> RunAsync(string input, List<(string name, string instructions, Func<string, string, Task<string>> fn)> agents)
    {
        if (agents.Count == 0) return "";
        string current = input;
        for (var i = 0; i < agents.Count && i < 5; i++)
        {
            var (name, _, fn) = agents[i];
            current = await fn(name, current);
        }
        return current;
    }
}

public sealed class GroupChatWorkflow
{
    private readonly Func<string, string, Task<string>> _chatFn;

    public GroupChatWorkflow(Func<string, string, Task<string>> chatFn) => _chatFn = chatFn;

    public async Task<WorkflowResult> RunAsync(string topic, List<(string name, string role)> participants, int rounds = 3)
    {
        var result = new WorkflowResult { WorkflowId = Guid.NewGuid().ToString("N")[..8] };
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        var conversation = new List<string> { $"Topic: {topic}" };

        for (var r = 0; r < rounds; r++)
        {
            foreach (var (name, role) in participants)
            {
                var prompt = $"Round {r + 1}. As {role}, respond to:\n{string.Join("\n", conversation.TakeLast(3))}";
                var step = new WorkflowStep { Name = $"{name}({role})", AgentName = name, Input = prompt, Status = WorkflowStepStatus.Running };
                var t0 = global::System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    step.Output = await _chatFn(name, prompt);
                    step.Status = WorkflowStepStatus.Done;
                    conversation.Add($"{name}({role}): {step.Output}");
                }
                catch (Exception ex) { step.Status = WorkflowStepStatus.Failed; step.Error = ex.Message; }
                step.LatencyMs = t0.ElapsedMilliseconds;
                result.Steps.Add(step);
            }
        }

        result.TotalLatencyMs = sw.ElapsedMilliseconds;
        return result;
    }
}
