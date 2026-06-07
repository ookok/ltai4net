using System.Text;
using LTAI.Core.Commands;
using LTAI.Core.Specs;
using Spectre.Console;

namespace LTAI.TUI.Services;

public sealed class SpecCommandService : ICommandService
{
    private readonly SpecService _specs;

    public SpecCommandService(SpecService specs)
    {
        _specs = specs;
    }

    public CommandResult Execute(Command command) => command switch
    {
        SpecCommand sc => HandleSpec(sc.Args),
        _ => new SuccessResult("ok"),
    };

    private CommandResult HandleSpec(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var subArgs = parts.Length > 1 ? parts[1] : "";

        return sub switch
        {
            "" or "list" => SpecList(),
            "new" or "create" => SpecNew(subArgs),
            "show" or "read" => SpecShow(subArgs),
            "edit" => SpecEdit(subArgs),
            "delete" or "rm" => SpecDelete(subArgs),
            "status" => SpecSetStatus(subArgs),
            "plan" => HandlePlan(subArgs),
            "tasks" => HandleTasks(subArgs),
            _ => new SuccessResult("用法: /spec list|new|show|edit|delete|status|plan|tasks"),
        };
    }

    private CommandResult SpecList()
    {
        var list = _specs.List();
        if (list.Count == 0)
            return new SuccessResult("[yellow]暂无 spec。使用 /spec new <name> 创建[/]");

        var sb = new StringBuilder();
        sb.AppendLine("[bold yellow]Specs[/]\n");
        foreach (var m in list)
        {
            var statusTag = m.Status switch
            {
                SpecStatus.Draft => "[grey]草稿[/]",
                SpecStatus.Clarified => "[blue]已澄清[/]",
                SpecStatus.Planned => "[cyan]已规划[/]",
                SpecStatus.Tasked => "[yellow]已拆解[/]",
                SpecStatus.Implementing => "[green]实现中[/]",
                SpecStatus.Done => "[green]已完成[/]",
                _ => "[grey]未知[/]",
            };
            sb.AppendLine($"  [cyan]{m.Name.EscapeMarkup(),-20}[/] {statusTag}  {m.Description.EscapeMarkup()}");
        }
        sb.AppendLine($"\n[grey]共 {list.Count} 个[/]");
        return new SuccessResult(sb.ToString());
    }

    private CommandResult SpecNew(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new SuccessResult("用法: /spec new <name>");
        if (_specs.Get(name) != null)
            return new SuccessResult($"[red]spec '{name.EscapeMarkup()}' 已存在[/]");

        _specs.WriteSpec(name, $"# {name}\n\n## 概述\n\n## 功能需求\n\n## 验收标准\n");
        return new SuccessResult($"[green]✅ spec '{name.EscapeMarkup()}' 已创建。使用 /spec edit {name.EscapeMarkup()} 编辑[/]");
    }

    private CommandResult SpecShow(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new SuccessResult("用法: /spec show <name>");
        var content = _specs.ReadSpec(name);
        if (content == null)
            return new SuccessResult($"[red]spec '{name.EscapeMarkup()}' 未找到[/]");
        var m = _specs.Get(name);
        var statusLine = m != null ? $"\n[grey]状态: {m.Status}  创建: {m.CreatedAt:yyyy-MM-dd}[/]" : "";
        return new SuccessResult(content.EscapeMarkup() + statusLine);
    }

    private CommandResult SpecEdit(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new SuccessResult("用法: /spec edit <name>");
        var content = _specs.ReadSpec(name);
        if (content == null)
            return new SuccessResult($"[red]spec '{name.EscapeMarkup()}' 未找到[/]");

        AnsiConsole.MarkupLine("[yellow]输入新内容（空行 = 保持原内容，/cancel = 取消）:[/]");
        var lines = new List<string>();
        while (true)
        {
            var line = Console.ReadLine() ?? "";
            if (line == "/cancel") return new SuccessResult("已取消");
            if (line == "") break;
            lines.Add(line);
        }
        if (lines.Count > 0)
        {
            _specs.WriteSpec(name, string.Join("\n", lines));
            return new SuccessResult($"[green]✅ spec '{name.EscapeMarkup()}' 已更新[/]");
        }
        return new SuccessResult("未作修改");
    }

    private CommandResult SpecDelete(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new SuccessResult("用法: /spec delete <name>");
        if (!_specs.Delete(name))
            return new SuccessResult($"[red]spec '{name.EscapeMarkup()}' 未找到[/]");
        return new SuccessResult($"[green]🗑️ spec '{name.EscapeMarkup()}' 已删除[/]");
    }

    private CommandResult SpecSetStatus(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return new SuccessResult("用法: /spec status <name> draft|clarified|planned|tasked|implementing|done");
        if (!Enum.TryParse<LTAI.Core.Specs.SpecStatus>(parts[1], ignoreCase: true, out var status))
            return new SuccessResult($"未知状态 '{parts[1].EscapeMarkup()}'。可选: draft, clarified, planned, tasked, implementing, done");
        _specs.SetStatus(parts[0], status);
        return new SuccessResult($"[green]✅ spec '{parts[0].EscapeMarkup()}' 状态已更新为 {status}[/]");
    }

    // ── /plan ──

    private CommandResult HandlePlan(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var name = parts.Length > 0 ? parts[0] : "";

        if (string.IsNullOrWhiteSpace(name))
        {
            var sb = new StringBuilder();
            sb.AppendLine("[bold yellow]已规划的 Specs[/]\n");
            foreach (var m in _specs.List().Where(m => m.Status >= SpecStatus.Planned))
            {
                var plan = _specs.ReadPlan(m.Name);
                var hasPlan = plan != null ? "📋" : "📄";
                sb.AppendLine($"  {hasPlan} [cyan]{m.Name.EscapeMarkup()}[/] — {m.Description.EscapeMarkup()}");
            }
            return new SuccessResult(sb.ToString().TrimEnd());
        }

        var content = _specs.ReadPlan(name);
        if (content == null)
            return new SuccessResult($"[yellow]'{name.EscapeMarkup()}' 尚无 plan。使用 /plan {name.EscapeMarkup()} <内容> 创建[/]");
        return new SuccessResult(content.EscapeMarkup());
    }

    private CommandResult HandleTasks(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var name = parts.Length > 0 ? parts[0] : "";

        if (string.IsNullOrWhiteSpace(name))
        {
            var sb = new StringBuilder();
            sb.AppendLine("[bold yellow]已拆解任务的 Specs[/]\n");
            foreach (var m in _specs.List().Where(m => m.Status >= SpecStatus.Tasked))
            {
                var tasks = _specs.ReadTasks(m.Name);
                var hasTasks = tasks != null ? "✅" : "📄";
                sb.AppendLine($"  {hasTasks} [cyan]{m.Name.EscapeMarkup()}[/] — {m.Description.EscapeMarkup()}");
            }
            return new SuccessResult(sb.ToString().TrimEnd());
        }

        var content = _specs.ReadTasks(name);
        if (content == null)
            return new SuccessResult($"[yellow]'{name.EscapeMarkup()}' 尚无 task。使用 /tasks {name.EscapeMarkup()} <内容> 创建[/]");
        return new SuccessResult(content.EscapeMarkup());
    }
}
