using LTAI.Agent.Workflows;
using LTAI.Core.Commands;
using Spectre.Console;

namespace LTAI.TUI.Services;

public sealed class WorkflowCommandService : ICommandService
{
    private readonly YAMLWorkflowRegistry? _workflowRegistry;

    public WorkflowCommandService(YAMLWorkflowRegistry? workflowRegistry)
    {
        _workflowRegistry = workflowRegistry;
    }

    public CommandResult Execute(Command command) => command switch
    {
        WorkflowCommand wc => HandleWorkflowCommand(wc.Args),
        _ => new SuccessResult("ok"),
    };

    private CommandResult HandleWorkflowCommand(string args)
    {
        var registry = _workflowRegistry;
        if (registry == null)
            return new SuccessResult("Workflow registry not initialized (YAMLWorkflowRegistry missing in DI)");

        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var subArgs = parts.Length > 1 ? parts[1].Trim() : "";

        return sub switch
        {
            "" or "list" => WorkflowList(registry),
            "reload" => WorkflowReload(registry, subArgs),
            "show" => WorkflowShow(registry, subArgs),
            "open" => WorkflowOpen(registry, subArgs),
            _ => new SuccessResult("用法: /workflow list | reload [name|*] | show <name> | open [name]"),
        };
    }

    private static CommandResult WorkflowList(YAMLWorkflowRegistry registry)
    {
        var list = registry.List();
        if (list.Count == 0)
            return new SuccessResult($"[yellow]暂无 workflow[/]  目录: {registry.WatchDirectory}\n" +
                                    "把 *.yaml / *.json 丢进该目录即可热加载");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Name");
        table.AddColumn("Type");
        table.AddColumn("V");
        table.AddColumn("Size");
        table.AddColumn("Loaded");
        table.AddColumn("Path");

        foreach (var w in list)
        {
            var size = w.SizeBytes switch
            {
                < 1024 => $"{w.SizeBytes} B",
                < 1024 * 1024 => $"{w.SizeBytes / 1024.0:F1} KB",
                _ => $"{w.SizeBytes / (1024.0 * 1024):F1} MB"
            };
            var loaded = w.LoadedAtUtc.ToLocalTime().ToString("HH:mm:ss");
            var fileName = System.IO.Path.GetFileName(w.FilePath);
            table.AddRow(
                $"[cyan]{w.Name.EscapeMarkup()}[/]",
                $"[grey]{w.Type.EscapeMarkup()}[/]",
                w.Version.ToString(),
                size,
                loaded,
                $"[grey]{fileName.EscapeMarkup()}[/]");
        }

        AnsiConsole.Write(table);
        return new SuccessResult($"[grey]共 {list.Count} 个 workflow · 目录: {registry.WatchDirectory}[/]");
    }

    private static CommandResult WorkflowReload(YAMLWorkflowRegistry registry, string name)
    {
        try
        {
            if (string.IsNullOrEmpty(name) || name == "*")
            {
                var all = registry.List();
                registry.ReloadAllAsync().GetAwaiter().GetResult();
                return new SuccessResult($"[green]✅ 已触发重载[/]  {all.Count} 个 workflow");
            }

            var dir = registry.WatchDirectory;
            var exts = new[] { ".yaml", ".yml", ".json" };
            string? matchPath = null;
            foreach (var ext in exts)
            {
                var p = System.IO.Path.Combine(dir, name + ext);
                if (System.IO.File.Exists(p)) { matchPath = p; break; }
            }
            if (matchPath == null)
                return new SuccessResult($"[red]❌ 找不到 workflow '{name}'[/]  目录: {dir}");

            registry.ReloadFileAsync(matchPath).GetAwaiter().GetResult();
            return new SuccessResult($"[green]✅ 已重载[/] [cyan]/{name}[/]");
        }
        catch (Exception ex)
        {
            return new SuccessResult($"[red]❌ 重载失败:[/] {ex.Message}");
        }
    }

    private static CommandResult WorkflowShow(YAMLWorkflowRegistry registry, string name)
    {
        if (string.IsNullOrEmpty(name))
            return new SuccessResult("用法: /workflow show <name>");

        var dir = registry.WatchDirectory;
        var exts = new[] { ".yaml", ".yml", ".json" };
        string? matchPath = null;
        foreach (var ext in exts)
        {
            var p = System.IO.Path.Combine(dir, name + ext);
            if (System.IO.File.Exists(p)) { matchPath = p; break; }
        }
        if (matchPath == null)
            return new SuccessResult($"[red]❌ 找不到 workflow '{name}'[/]  目录: {dir}");

        try
        {
            var content = System.IO.File.ReadAllText(matchPath);
            var lines = content.Split('\n');
            var preview = string.Join("\n", lines.Take(60));
            var truncated = lines.Length > 60 ? $"\n[grey]... ({lines.Length - 60} more lines)[/]" : "";
            AnsiConsole.Write(new Panel(new Markup(preview.EscapeMarkup()))
                .Header($"[green] {name} ({lines.Length} lines) [/]")
                .Border(BoxBorder.Rounded)
                .Expand());
            return new SuccessResult($"[grey]{matchPath}[/]{truncated}");
        }
        catch (Exception ex)
        {
            return new SuccessResult($"[red]❌ 读取失败:[/] {ex.Message}");
        }
    }

    private static CommandResult WorkflowOpen(YAMLWorkflowRegistry registry, string name)
    {
        if (string.IsNullOrEmpty(name))
            return new SuccessResult("用法: /workflow open <name>  (用系统默认程序打开文件)");

        var dir = registry.WatchDirectory;
        var exts = new[] { ".yaml", ".yml", ".json" };
        string? matchPath = null;
        foreach (var ext in exts)
        {
            var p = System.IO.Path.Combine(dir, name + ext);
            if (System.IO.File.Exists(p)) { matchPath = p; break; }
        }
        if (matchPath == null)
            return new SuccessResult($"[red]❌ 找不到 workflow '{name}'[/]  目录: {dir}");

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = matchPath,
                UseShellExecute = true,
            });
            return new SuccessResult($"[green]✅ 已用系统默认程序打开[/] [cyan]{matchPath}[/]");
        }
        catch (Exception ex)
        {
            return new SuccessResult($"[red]❌ 打开失败:[/] {ex.Message}");
        }
    }
}
