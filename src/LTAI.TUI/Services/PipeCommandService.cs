using LTAI.Agent.Workflows;
using LTAI.TUI.Commands;
using Spectre.Console;

namespace LTAI.TUI.Services;

public sealed class PipeCommandService : ICommandService
{
    private readonly AgentWorkflows? _pipes;
    private readonly YAMLWorkflowRegistry? _workflowRegistry;

    public PipeCommandService(
        AgentWorkflows? pipes,
        YAMLWorkflowRegistry? workflowRegistry)
    {
        _pipes = pipes;
        _workflowRegistry = workflowRegistry;
    }

    public CommandResult Execute(Command command) => command switch
    {
        PipeCommand pc => HandlePipeCommand(pc.Args),
        _ => new SuccessResult("ok"),
    };

    private CommandResult HandlePipeCommand(string args)
    {
        var pipes = _pipes;
        var registry = _workflowRegistry;
        if (pipes == null)
            return new SuccessResult("Pipes (AgentWorkflows) not initialized");

        var parts = args.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var subArgs1 = parts.Length > 1 ? parts[1].Trim() : "";
        var subArgs2 = parts.Length > 2 ? parts[2].Trim() : "";

        return sub switch
        {
            "" or "list" => PipelinesList(registry),
            "run" => PipeRun(pipes, registry, subArgs1, subArgs2),
            "stop" => new SuccessResult("Run cancellation via tools, not /pipe stop"),
            _ => new SuccessResult("用法: /pipe list | run <preset> [task] | stop <id>"),
        };
    }

    private static CommandResult PipelinesList(YAMLWorkflowRegistry? registry)
    {
        if (registry == null)
            return new SuccessResult("[yellow]暂无 pipeline 配置[/]  请创建 sequential/concurrent JSON 文件");

        var info = registry.List();
        var pipelinePresets = info.Where(w => w.Type is "sequential" or "concurrent").ToList();
        if (pipelinePresets.Count == 0)
            return new SuccessResult("[yellow]暂无 pipeline 配置[/]  创建 sequential.json / concurrent.json 后重试");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Preset");
        table.AddColumn("Type");
        table.AddColumn("V");
        table.AddColumn("Agents/Sources");
        table.AddColumn("Path");

        foreach (var p in pipelinePresets)
        {
            var cfg = registry.TryGetPipelineConfig(p.Name);
            var agents = cfg?.Agents ?? [];
            var agentsStr = agents.Count > 0
                ? string.Join(", ", agents.Select(a => $"[cyan]{a}[/]"))
                : "[grey](empty)[/]";
            var fileName = System.IO.Path.GetFileName(p.FilePath);
            table.AddRow(
                $"[cyan]{p.Name.EscapeMarkup()}[/]",
                $"[grey]{p.Type.EscapeMarkup()}[/]",
                p.Version.ToString(),
                agentsStr,
                $"[grey]{fileName.EscapeMarkup()}[/]");
        }

        AnsiConsole.Write(table);
        return new SuccessResult($"[grey]共 {pipelinePresets.Count} 个 pipeline · /pipe run <name> [task] 执行[/]");
    }

    private static CommandResult PipeRun(
        AgentWorkflows pipes,
        YAMLWorkflowRegistry? registry,
        string presetName,
        string task)
    {
        if (string.IsNullOrEmpty(presetName))
            return new SuccessResult("用法: /pipe run <preset> [task]  — 例如: /pipe run sequential \"写一篇博客\"");

        if (registry == null)
            return new SuccessResult("Workflow registry not available; cannot resolve pipeline preset");

        var cfg = registry.TryGetPipelineConfig(presetName);
        if (cfg == null)
        {
            var info = registry.List();
            var pipeNames = info.Where(w => w.Type is "sequential" or "concurrent").Select(w => w.Name).ToList();
            var hint = pipeNames.Count > 0
                ? $"可用: {string.Join(", ", pipeNames)}"
                : "没有可用 pipeline。创建 sequential.json / concurrent.json 后重试";
            return new SuccessResult($"未知 pipeline '[red]{presetName}[/]'  {hint}");
        }

        var defaultTask = string.IsNullOrEmpty(task)
            ? cfg.DefaultTask ?? "请根据预设 agents 列表完成任务"
            : task;

        if (cfg.Type == "concurrent")
        {
            AnsiConsole.MarkupLine($"[yellow]⏳[/] 并发 pipeline [cyan]{presetName}[/] on: [grey]{defaultTask.EscapeMarkup()}[/]");
            var result = Task.Run(() => pipes.RunConcurrentAsync([presetName], defaultTask, ct: default)).GetAwaiter().GetResult();
            AnsiConsole.MarkupLine(result);
            return new SuccessResult($"[green]✅ 并发完成[/] 请查看上方结果");
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]⏳[/] 顺序 pipeline [cyan]{presetName}[/] on: [grey]{defaultTask.EscapeMarkup()}[/]");
            var result = Task.Run(() => pipes.RunSequentialAsync([presetName], defaultTask, ct: default)).GetAwaiter().GetResult();
            AnsiConsole.MarkupLine(result);
            return new SuccessResult($"[green]✅ 顺序完成[/] 请查看上方结果");
        }
    }
}
