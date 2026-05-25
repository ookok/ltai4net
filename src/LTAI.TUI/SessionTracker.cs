using Spectre.Console.Rendering;
using Spectre.Console;
using LTAI.AI.Interfaces;
using LTAI.AI.Governors;
using LTAI.DNA;

namespace LTAI.TUI;

public sealed class SessionTracker
{
    public string SessionId { get; } = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    public DateTime StartedAt { get; } = DateTime.Now;
    public int TotalTurns { get; set; }
    public int TotalTokens { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public double AvgLatencyMs { get; set; }
    public int ContextWindowUsed { get; set; }
    public int MaxContextWindow { get; set; } = 128000;
    public List<(string agent, string action)> AgentTrace { get; } = new();
    public List<TaskEntry> ActiveTasks { get; } = new();
    public DateTime LastActivity { get; set; }

    public void RecordTurn(int inputTokens, int outputTokens, double latencyMs)
    {
        TotalTurns++;
        InputTokens += inputTokens;
        OutputTokens += outputTokens;
        TotalTokens = InputTokens + OutputTokens;
        AvgLatencyMs = (AvgLatencyMs * (TotalTurns - 1) + latencyMs) / TotalTurns;
        LastActivity = DateTime.Now;
    }

    public void RecordAgentAction(string agent, string action) =>
        AgentTrace.Add((agent, action));

    public void AddTask(string task, string status = "pending") =>
        ActiveTasks.Add(new TaskEntry { Name = task, Status = status, StartedAt = DateTime.Now });

    public IRenderable RenderPanel(ILivingTreeSystem lts, DNAOrchestrator? dna)
    {
        var elapsed = DateTime.Now - StartedAt;
        var ctxPercent = MaxContextWindow > 0 ? (double)ContextWindowUsed / MaxContextWindow * 100 : 0;

        return Panel($$"""
            [cyan]Session:[/] {{SessionId}}
            [cyan]Uptime:[/] {{elapsed.Hours}}h {{elapsed.Minutes}}m {{elapsed.Seconds}}s
            [cyan]Turns:[/] {{TotalTurns}}  [grey]Last: {{LastActivity:HH:mm:ss}}[/]

            [yellow]Tokens:[/] in={{InputTokens}} out={{OutputTokens}} total={{TotalTokens}}
            [yellow]Context:[/] {{ContextWindowUsed}}/{{MaxContextWindow}} ({{ctxPercent:F0}}%)
            [yellow]Latency:[/] {{AvgLatencyMs:F0}}ms avg

            [green]Agent:[/] {{lts.Mode}}  [green]DNA:[/] {{(dna != null ? dna.Consciousness.State.Level.ToString() : "off")}}
            [green]Tasks:[/] {{ActiveTasks.Count(t => t.Status == "running")}} active, {{ActiveTasks.Count(t => t.Status == "done")}} done

            [grey]Trace:[/] {{string.Join(" > ", AgentTrace.TakeLast(6).Select(a => $"{a.agent}:{a.action}"))}}
            """, "[cyan]Session · Agent · Tokens[/]");
    }

    private static Panel Panel(string content, string header)
    {
        var panel = new Panel(content);
        panel.Header = new PanelHeader(header);
        return panel;
    }
}

public sealed class TaskEntry
{
    public string Name { get; init; } = "";
    public string Status { get; set; } = "pending";
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
    public string? Result { get; set; }
}

public sealed class TaskPulseRenderer
{
    private static readonly string[] SpinnerFrames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
    private static readonly string[] PulseBar = { "▁", "▂", "▃", "▄", "▅", "▆", "▇", "█" };
    private int _frame;
    private int _pulse;

    public IRenderable RenderTasks(List<TaskEntry> tasks)
    {
        if (tasks.Count == 0)
            return new Markup("[grey](No active tasks)[/]");

        var sb = new System.Text.StringBuilder();
        _frame = (_frame + 1) % SpinnerFrames.Length;
        _pulse = (_pulse + 1) % PulseBar.Length;

        foreach (var task in tasks.TakeLast(8))
        {
            var icon = task.Status switch
            {
                "pending" => "[grey]○[/]",
                "running" => $"[cyan]{SpinnerFrames[_frame]}[/]",
                "done" => "[green]✓[/]",
                "failed" => "[red]✗[/]",
                _ => "[grey]?[/]"
            };

            var pulse = task.Status == "running" ? new string('█', _pulse + 1) + new string('░', 7 - _pulse) : "";
            var elapsed = task.CompletedAt != null
                ? $"{task.CompletedAt.Value - task.StartedAt:hh\\:mm\\:ss}"
                : $"{DateTime.Now - task.StartedAt:hh\\:mm\\:ss}";

            sb.AppendLine($"{icon} [white]{task.Name}[/] [grey]{elapsed}[/] [cyan]{pulse}[/]");
            if (!string.IsNullOrEmpty(task.Result))
                sb.AppendLine($"   [grey]{task.Result[..Math.Min(task.Result.Length, 80)]}[/]");
        }

        return new Markup(sb.ToString().TrimEnd());
    }

    public IRenderable RenderPhaseIndicator(string currentPhase, string[] phases)
    {
        var sb = new System.Text.StringBuilder();
        var currentIdx = Array.IndexOf(phases, currentPhase);
        if (currentIdx < 0) currentIdx = 0;

        for (var i = 0; i < phases.Length; i++)
        {
            if (i < currentIdx)
                sb.Append($"[green]● {phases[i]}[/] ");
            else if (i == currentIdx)
                sb.Append($"[cyan blink]{SpinnerFrames[_frame]} {phases[i]}[/] ");
            else
                sb.Append($"[grey]○ {phases[i]}[/] ");
            if (i < phases.Length - 1) sb.Append("→ ");
        }

        return new Markup(sb.ToString());
    }
}
