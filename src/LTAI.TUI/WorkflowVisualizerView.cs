// Copyright (c) LTAI. All rights reserved.

using System.Text;
using LTAI.Agent.Workflows;
using Spectre.Console;

namespace LTAI.TUI;

public sealed class WorkflowVisualizerView
{
    private readonly YAMLWorkflowRegistry? _registry;

    public WorkflowVisualizerView(YAMLWorkflowRegistry? registry = null)
    {
        _registry = registry;
    }

    public void Render()
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule("[bold]Workflow Visualizer[/]") { Style = Style.Parse("bold") });
            AnsiConsole.MarkupLine("[dim]Commands: select <name|#>  |  q=quit[/]\n");

            var list = _registry?.List() ?? [];
            if (list.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]No workflows loaded[/]");
                AnsiConsole.MarkupLine("[grey]Press any key to return...[/]");
                Console.ReadKey(true);
                break;
            }

            RenderSummaryTable(list);

            var input = AnsiConsole.Ask<string>("\n[grey]>[/] ").Trim();
            if (input is "q" or "quit" or "exit") break;

            var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToLowerInvariant();
            var arg = parts.Length > 1 ? parts[1] : null;

            if (cmd == "select" && arg != null)
            {
                var found = ResolveWorkflow(list, arg);
                if (found == null)
                {
                    AnsiConsole.MarkupLine($"[red]Workflow not found: {arg.EscapeMarkup()}[/]");
                    PromptContinue();
                    continue;
                }
                RenderWorkflowDetail(found.Value);
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Unknown command: {cmd.EscapeMarkup()}[/]");
                PromptContinue();
            }
        }
    }

    private static void RenderSummaryTable(IReadOnlyList<WorkflowInfo> list)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]#[/]");
        table.AddColumn("[bold]Name[/]");
        table.AddColumn("[bold]Type[/]");
        table.AddColumn("[bold]V[/]");
        table.AddColumn("[bold]Size[/]");
        table.AddColumn("[bold]Loaded[/]");
        table.AddColumn("[bold]File[/]");

        for (int i = 0; i < list.Count; i++)
        {
            var w = list[i];
            var size = w.SizeBytes switch
            {
                < 1024 => $"{w.SizeBytes} B",
                < 1024 * 1024 => $"{w.SizeBytes / 1024.0:F1} KB",
                _ => $"{w.SizeBytes / (1024.0 * 1024):F1} MB"
            };
            table.AddRow(
                (i + 1).ToString(),
                $"[cyan]{w.Name.EscapeMarkup()}[/]",
                $"[grey]{w.Type.EscapeMarkup()}[/]",
                w.Version.ToString(),
                size,
                w.LoadedAtUtc.ToLocalTime().ToString("HH:mm:ss"),
                $"[grey]{Path.GetFileName(w.FilePath).EscapeMarkup()}[/]");
        }
        AnsiConsole.Write(table);
    }

    private static WorkflowInfo? ResolveWorkflow(IReadOnlyList<WorkflowInfo> list, string selector)
    {
        if (int.TryParse(selector, out var idx) && idx >= 1 && idx <= list.Count)
            return list[idx - 1];

        return list.FirstOrDefault(w =>
            w.Name.Equals(selector, StringComparison.OrdinalIgnoreCase));
    }

    private void RenderWorkflowDetail(WorkflowInfo info)
    {
        AnsiConsole.Clear();
        RenderFlowChart(info);
        AnsiConsole.MarkupLine("\n[dim]Press any key to return...[/]");
        Console.ReadKey(true);
    }

    private void RenderFlowChart(WorkflowInfo info)
    {
        var rule = new Rule($"[bold]{info.Name.EscapeMarkup()}[/]  [grey]({info.Type.EscapeMarkup()}  v{info.Version})[/]")
            { Style = Style.Parse("bold cyan") };
        AnsiConsole.Write(rule);

        var metaPanel = new Panel(
                new Markup(
                    $"[dim]Path:[/]  {info.FilePath.EscapeMarkup()}\n" +
                    $"[dim]Loaded:[/] {info.LoadedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" +
                    $"[dim]Size:[/]   {info.SizeBytes} bytes"))
            .Border(BoxBorder.Rounded)
            .Header("[bold]Info[/]")
            .Expand();
        AnsiConsole.Write(metaPanel);

        switch (info.Type.ToLowerInvariant())
        {
            case "decision-tree":
                RenderDecisionTree(info.Name);
                break;
            case "sequential":
                RenderPipeline(info, false);
                break;
            case "concurrent":
                RenderPipeline(info, true);
                break;
            default:
                RenderMafWorkflow(info);
                break;
        }
    }

    private void RenderDecisionTree(string name)
    {
        var cfg = _registry?.GetDecisionTreeConfig(name);
        if (cfg == null || cfg == DecisionTreeConfig.Default)
        {
            AnsiConsole.MarkupLine("\n[yellow]No decision-tree config loaded[/]");
            return;
        }

        // Parameters table
        var paramTable = new Table().Border(TableBorder.Rounded);
        paramTable.AddColumn("[bold]Parameter[/]");
        paramTable.AddColumn("[bold]Value[/]");
        paramTable.AddRow("topK", cfg.TopK.ToString());
        paramTable.AddRow("confidenceMarginThreshold", cfg.ConfidenceMarginThreshold.ToString("F2"));
        paramTable.AddRow("minTopScoreThreshold", cfg.MinTopScoreThreshold.ToString("F2"));
        paramTable.AddRow("ambiguousFallback", cfg.AmbiguousFallback.EscapeMarkup());
        paramTable.AddRow("minAcceptableScore", cfg.MinAcceptableScore.ToString("F2"));

        AnsiConsole.Write(new Panel(paramTable)
            .Border(BoxBorder.Rounded)
            .Header("[bold]Parameters[/]")
            .Expand());

        // Routing tree ASCII
        var treeSb = new StringBuilder();
        treeSb.AppendLine(" [green]Input[/]");
        treeSb.AppendLine("   │");
        treeSb.AppendLine("   ▼");
        treeSb.AppendLine(" ┌──────────────────────────┐");
        treeSb.AppendLine($" │  Embedding Top-K          │── topK=[cyan]{cfg.TopK}[/]");
        treeSb.AppendLine(" └────────────┬─────────────┘");
        treeSb.AppendLine("              │");
        treeSb.AppendLine("              ▼");
        treeSb.AppendLine(" ┌──────────────────────────┐");
        treeSb.AppendLine(" │  Confidence Check         │");
        treeSb.AppendLine(" └────────────┬─────────────┘");
        treeSb.AppendLine("              │");
        treeSb.AppendLine($" ├─ [green]Confident[/] (margin ≥ {cfg.ConfidenceMarginThreshold:F2})");
        treeSb.AppendLine($" │    └──→ Route to top-1 agent");
        treeSb.AppendLine($" ├─ [yellow]Ambiguous[/]");
        treeSb.AppendLine($" │    └──→ Fallback: [cyan]{cfg.AmbiguousFallback}[/]");
        treeSb.AppendLine($" └─ [red]Low Score[/] (top-1 < {cfg.MinTopScoreThreshold:F2})");
        treeSb.AppendLine($"      └──→ Fallback: [cyan]{cfg.AmbiguousFallback}[/]");

        AnsiConsole.Write(new Panel(new Markup(treeSb.ToString()))
            .Border(BoxBorder.Rounded)
            .Header("[bold]Routing Tree[/]")
            .Expand());

        // Candidates
        var candidatesSb = new StringBuilder();
        if (cfg.Candidates.Count == 0)
        {
            candidatesSb.AppendLine(" [grey](all agents)[/]");
        }
        else
        {
            for (int i = 0; i < cfg.Candidates.Count; i++)
            {
                var prefix = i == cfg.Candidates.Count - 1 ? "└──" : "├──";
                candidatesSb.AppendLine($" {prefix} {cfg.Candidates[i].EscapeMarkup()}");
            }
        }

        AnsiConsole.Write(new Panel(new Markup(candidatesSb.ToString()))
            .Border(BoxBorder.Rounded)
            .Header("[bold]Candidates[/]")
            .Expand());

        // MCP Triggers
        var mcpSb = new StringBuilder();
        if (cfg.McpTriggers.Count == 0)
        {
            mcpSb.AppendLine(" [grey](none)[/]");
        }
        else
        {
            foreach (var t in cfg.McpTriggers)
            {
                var desc = !string.IsNullOrEmpty(t.Description) ? $"  [dim]{t.Description.EscapeMarkup()}[/]" : "";
                mcpSb.AppendLine($" ├── [cyan]{t.Pattern.EscapeMarkup()}[/] → [green]{t.Workflow.EscapeMarkup()}[/]{desc}");
            }
        }

        AnsiConsole.Write(new Panel(new Markup(mcpSb.ToString()))
            .Border(BoxBorder.Rounded)
            .Header("[bold]MCP Triggers[/]")
            .Expand());
    }

    private void RenderPipeline(WorkflowInfo info, bool isConcurrent)
    {
        var cfg = _registry?.TryGetPipelineConfig(info.Name);
        if (cfg == null)
        {
            AnsiConsole.MarkupLine("\n[yellow]No pipeline config loaded[/]");
            return;
        }

        var flowSb = new StringBuilder();

        if (cfg.Steps.Count > 0)
        {
            RenderNestedSteps(flowSb, cfg.Steps, isConcurrent);
        }
        else if (cfg.Agents.Count > 0)
        {
            RenderFlatAgents(flowSb, cfg.Agents, isConcurrent);
        }
        else
        {
            flowSb.AppendLine(" [grey](empty pipeline)[/]");
        }

        if (!string.IsNullOrEmpty(cfg.DefaultTask))
        {
            flowSb.AppendLine($"\n [dim]Default Task:[/] {cfg.DefaultTask.EscapeMarkup()}");
        }

        var style = isConcurrent ? "Concurrent (fan-out)" : "Sequential (pipeline)";
        AnsiConsole.Write(new Panel(new Markup(flowSb.ToString()))
            .Border(BoxBorder.Rounded)
            .Header($"[bold]{style}[/]")
            .Expand());
    }

    private static void RenderFlatAgents(StringBuilder sb, IReadOnlyList<string> agents, bool isConcurrent)
    {
        sb.AppendLine(" [green]Input[/]");

        if (isConcurrent)
        {
            sb.AppendLine("   │");
            sb.AppendLine("   ┌────┬────┬────┐");
            sb.AppendLine("   │    │    │    │");
            sb.AppendLine("   ▼    ▼    ▼    ▼");

            for (int i = 0; i < agents.Count; i++)
            {
                var label = agents[i].EscapeMarkup();
                if (label.Length > 10) label = label[..10];
                sb.AppendLine($" ┌──────────┐");
                sb.AppendLine($" │ {label,-10} │");
                if (i < agents.Count - 1)
                    sb.AppendLine($" └────┬─────┘");
                else
                    sb.AppendLine($" └────┬─────┘");
            }
            sb.AppendLine("   └────┴────┴────┘");
            sb.AppendLine("   │");
            sb.AppendLine("   ▼");
            sb.AppendLine(" [green]Output (aggregated)[/]");
        }
        else
        {
            foreach (var agent in agents)
            {
                sb.AppendLine("   │");
                sb.AppendLine("   ▼");
                var label = agent.EscapeMarkup();
                var pad = Math.Max(0, 14 - label.Length);
                sb.AppendLine($" ┌──────────────────┐");
                sb.AppendLine($" │ {label}{new string(' ', pad)} │");
                sb.AppendLine($" └──────────────────┘");
            }
            sb.AppendLine("   │");
            sb.AppendLine("   ▼");
            sb.AppendLine(" [green]Output[/]");
        }
    }

    private static void RenderNestedSteps(StringBuilder sb, IReadOnlyList<PipelineStep> steps, bool isConcurrent)
    {
        sb.AppendLine(" [green]Input[/]");

        if (isConcurrent)
        {
            sb.AppendLine("   │");
            sb.AppendLine("   ┌────┬────┬────┐");
            sb.AppendLine("   │    │    │    │");
            sb.AppendLine("   ▼    ▼    ▼    ▼");
        }

        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var stepLabel = !string.IsNullOrEmpty(step.Name)
                ? step.Name.EscapeMarkup()
                : step.Type.EscapeMarkup();

            var icon = step.Type switch
            {
                "handoff" => "→",
                "sequential" => "⇢",
                "concurrent" => "⇉",
                _ => "●"
            };

            if (isConcurrent)
            {
                var connector = i == steps.Count - 1 ? "└──" : "├──";
                sb.AppendLine($" {connector} [{GetStepColor(step.Type)}]{icon} {stepLabel}[/]");
                foreach (var agent in step.Agents)
                    sb.AppendLine($" │    [dim]Agent:[/] {agent.EscapeMarkup()}");

                if (step.Steps.Count > 0)
                {
                    RenderNestedSteps(sb, step.Steps,
                        string.Equals(step.Type, "concurrent", StringComparison.OrdinalIgnoreCase));
                }
            }
            else
            {
                sb.AppendLine("   │");
                sb.AppendLine("   ▼");
                var label = $"{icon} {stepLabel}";
                var pad = Math.Max(0, 16 - label.Length);
                sb.AppendLine($" ┌──────────────────┐");
                sb.AppendLine($" │ [{GetStepColor(step.Type)}]{label}{new string(' ', pad)}[/] │");
                sb.AppendLine($" └──────────────────┘");

                foreach (var agent in step.Agents)
                    sb.AppendLine($"    [dim]Agent:[/] {agent.EscapeMarkup()}");

                if (step.Steps.Count > 0)
                {
                    RenderNestedSteps(sb, step.Steps,
                        string.Equals(step.Type, "concurrent", StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        if (!isConcurrent)
        {
            sb.AppendLine("   │");
            sb.AppendLine("   ▼");
            sb.AppendLine(" [green]Output[/]");
        }
    }

    private static string GetStepColor(string stepType) => stepType switch
    {
        "handoff" => "cyan",
        "sequential" => "green",
        "concurrent" => "yellow",
        _ => "grey"
    };

    private static void RenderMafWorkflow(WorkflowInfo info)
    {
        try
        {
            if (!File.Exists(info.FilePath))
            {
                AnsiConsole.MarkupLine("\n[yellow]File not found on disk[/]");
                return;
            }

            var content = File.ReadAllText(info.FilePath);
            var actions = ExtractMafActions(content);

            if (actions.Count == 0)
            {
                AnsiConsole.MarkupLine("\n[grey]Declarative workflow (MAF) — no structured action preview available[/]");
                return;
            }

            var flowSb = new StringBuilder();

            // Trigger
            var trigger = ExtractMafTrigger(content);
            flowSb.AppendLine($" [green]Start[/]  [dim](trigger: {trigger.EscapeMarkup()})[/]");

            // Render action chain
            foreach (var (kind, id, isCondition) in actions)
            {
                flowSb.AppendLine("   │");
                flowSb.AppendLine("   ▼");

                if (isCondition)
                {
                    flowSb.AppendLine($" ┌──────────────────┐");
                    flowSb.AppendLine($" │ [yellow]ConditionGroup[/]    │");
                    var idDisp = !string.IsNullOrEmpty(id) ? $" ({id.EscapeMarkup()})" : "";
                    flowSb.AppendLine($" └──────────────────┘{idDisp}");

                    // Extract branches from condition
                    var branches = ExtractMafConditionBranches(content, id);
                    foreach (var branch in branches)
                    {
                        flowSb.AppendLine($"   ├── [dim]{branch.EscapeMarkup()}[/]");
                    }
                }
                else
                {
                    var kindColor = kind switch
                    {
                        "SetVariable" => "cyan",
                        "SendActivity" => "green",
                        _ => "grey"
                    };
                    var idDisp = !string.IsNullOrEmpty(id) ? $" [dim]({id.EscapeMarkup()})[/]" : "";
                    flowSb.AppendLine($" ┌──────────────────┐");
                    flowSb.AppendLine($" │ [{kindColor}]{kind,-16}[/]│{idDisp}");
                    flowSb.AppendLine($" └──────────────────┘");
                }
            }

            flowSb.AppendLine("   │");
            flowSb.AppendLine("   ▼");
            flowSb.AppendLine(" [green]End[/]");

            AnsiConsole.Write(new Panel(new Markup(flowSb.ToString()))
                .Border(BoxBorder.Rounded)
                .Header("[bold]MAF Workflow Flow[/]")
                .Expand());
        }
        catch
        {
            AnsiConsole.MarkupLine("\n[red]Error reading workflow file[/]");
        }
    }

    private static string ExtractMafTrigger(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("kind:") && !trimmed.StartsWith("kind: Workflow"))
            {
                var val = trimmed["kind:".Length..].Trim().Trim('\'', '"');
                if (!string.IsNullOrEmpty(val) && val != "Workflow")
                    return val;
            }
        }
        return "OnConversationStart";
    }

    private static List<(string Kind, string Id, bool IsCondition)> ExtractMafActions(string content)
    {
        var result = new List<(string Kind, string Id, bool IsCondition)>();
        var lines = content.Split('\n');
        string? pendingKind = null;
        string? pendingId = null;
        var depth = -1;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                continue;

            var leading = line.Length - trimmed.Length;

            if (trimmed.StartsWith("kind:"))
            {
                // Flush previous
                if (pendingKind != null && depth <= leading)
                {
                    var isCond = pendingKind == "ConditionGroup";
                    if (!isCond || pendingKind != "ConditionGroup")
                        result.Add((pendingKind, pendingId ?? "", isCond));
                    pendingKind = null;
                    pendingId = null;
                }

                var kind = trimmed["kind:".Length..].Trim().Trim('\'', '"');
                if (kind is "SetVariable" or "SendActivity" or "ConditionGroup")
                {
                    pendingKind = kind;
                    depth = leading;
                }
            }
            else if (trimmed.StartsWith("id:") && pendingKind != null)
            {
                pendingId = trimmed["id:".Length..].Trim().Trim('\'', '"');
            }
        }

        // Flush last
        if (pendingKind != null)
        {
            var isCond = pendingKind == "ConditionGroup";
            result.Add((pendingKind, pendingId ?? "", isCond));
        }

        return result;
    }

    private static List<string> ExtractMafConditionBranches(string content, string? groupId)
    {
        var branches = new List<string>();
        var lines = content.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();

            // Match condition branch entries
            if (trimmed.StartsWith("id: cond_"))
            {
                var id = trimmed["id:".Length..].Trim().Trim('\'', '"');
                // Look ahead for condition description
                var desc = "";
                for (int j = i + 1; j < Math.Min(i + 5, lines.Length); j++)
                {
                    var t = lines[j].TrimStart();
                    if (t.StartsWith("condition:"))
                    {
                        var condText = t["condition:".Length..].Trim().Trim('>', '-', ' ');
                        if (condText.Length > 60)
                            condText = condText[..57] + "...";
                        desc = condText;
                        break;
                    }
                    if (t.StartsWith("id:") || t.StartsWith("kind:"))
                        break;
                }

                var label = id.Replace("cond_", "");
                if (!string.IsNullOrEmpty(desc))
                    branches.Add($"{label}: [dim]{desc.EscapeMarkup()}[/]");
                else
                    branches.Add(label);
            }
        }

        return branches;
    }

    private static void PromptContinue()
    {
        AnsiConsole.MarkupLine("[grey]Press any key...[/]");
        Console.ReadKey(true);
    }
}
