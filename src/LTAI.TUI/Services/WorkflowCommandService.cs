using System.Text;
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

    public Task<CommandResult> ExecuteAsync(Command command) => command switch
    {
        WorkflowCommand wc => HandleWorkflowCommandAsync(wc.Args),
        _ => Task.FromResult<CommandResult>(new SuccessResult("ok")),
    };

    private async Task<CommandResult> HandleWorkflowCommandAsync(string args)
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
            "reload" => await WorkflowReloadAsync(registry, subArgs).ConfigureAwait(false),
            "show" => WorkflowShow(registry, subArgs),
            "open" => WorkflowOpen(registry, subArgs),
            "create" or "new" => WorkflowCreate(registry, subArgs),
            "edit" => WorkflowEdit(registry, subArgs),
            "delete" or "rm" => WorkflowDelete(registry, subArgs),
            _ => new SuccessResult("用法: /workflow list | reload [name|*] | show <name> | open [name] | create <name> [type] | edit <name> | delete <name>"),
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

    private static async Task<CommandResult> WorkflowReloadAsync(YAMLWorkflowRegistry registry, string name)
    {
        try
        {
            if (string.IsNullOrEmpty(name) || name == "*")
            {
                var all = registry.List();
                await registry.ReloadAllAsync().ConfigureAwait(false);
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

            await registry.ReloadFileAsync(matchPath).ConfigureAwait(false);
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

    private static CommandResult WorkflowCreate(YAMLWorkflowRegistry registry, string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var name = parts.Length > 0 ? parts[0] : "";
        var type = parts.Length > 1 ? parts[1].ToLowerInvariant() : "sequential";

        if (string.IsNullOrEmpty(name))
            return new SuccessResult("[yellow]用法: /workflow create <name> [sequential|concurrent|decision-tree][/]");

        var dir = registry.WatchDirectory;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var path = System.IO.Path.Combine(dir, name + ".yaml");
        if (System.IO.File.Exists(path))
            return new SuccessResult($"[red]文件已存在: {path}[/]");

        var template = type switch
        {
            "decision-tree" => GenerateDecisionTreeTemplate(name),
            "concurrent" => GenerateConcurrentTemplate(name),
            _ => GenerateSequentialTemplate(name),
        };

        try
        {
            System.IO.File.WriteAllText(path, template);
            return new SuccessResult($"[green]✅ 已创建 {type} workflow: {path}[/]\n[dim]编辑后保存即可热加载[/]");
        }
        catch (Exception ex)
        {
            return new SuccessResult($"[red]❌ 创建失败:[/] {ex.Message}");
        }
    }

    private static CommandResult WorkflowEdit(YAMLWorkflowRegistry registry, string name)
    {
        if (string.IsNullOrEmpty(name))
            return new SuccessResult("[yellow]用法: /workflow edit <name>[/]");

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

        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[bold]编辑 {name}[/]"));

        var content = System.IO.File.ReadAllText(matchPath);
        var lines = content.Split('\n');
        AnsiConsole.MarkupLine($"[grey]当前内容 ({lines.Length} 行):[/]\n");
        for (int i = 0; i < lines.Length; i++)
            AnsiConsole.MarkupLine($"[grey]{i + 1,3}:[/] {lines[i].EscapeMarkup()}");

        AnsiConsole.MarkupLine("\n[dim]输入新内容 (空行结束, .abort 取消):[/]");
        var newLines = new List<string>();
        while (true)
        {
            var line = Console.ReadLine();
            if (string.IsNullOrEmpty(line)) break;
            if (line == ".abort") return new SuccessResult("[yellow]已取消编辑[/]");
            newLines.Add(line);
        }

        if (newLines.Count > 0)
        {
            System.IO.File.WriteAllText(matchPath, string.Join("\n", newLines) + "\n");
            return new SuccessResult($"[green]✅ 已更新 {name} ({newLines.Count} 行)[/]");
        }
        return new SuccessResult("[yellow]内容未更改[/]");
    }

    private static CommandResult WorkflowDelete(YAMLWorkflowRegistry registry, string name)
    {
        if (string.IsNullOrEmpty(name))
            return new SuccessResult("[yellow]用法: /workflow delete <name>[/]");

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
            System.IO.File.Delete(matchPath);
            return new SuccessResult($"[green]✅ 已删除 workflow: {matchPath}[/]");
        }
        catch (Exception ex)
        {
            return new SuccessResult($"[red]❌ 删除失败:[/] {ex.Message}");
        }
    }

    private static string GenerateSequentialTemplate(string name)
    {
        return
            "kind: Workflow\n" +
           $"name: {name}\n" +
            "type: sequential\n" +
            "steps:\n" +
            "  - handoff:\n" +
            "      agent: LTAI-Chat\n" +
            "      input: \"{{{input}}}\"\n";
    }

    private static string GenerateConcurrentTemplate(string name)
    {
        return
            "kind: Workflow\n" +
           $"name: {name}\n" +
            "type: concurrent\n" +
            "steps:\n" +
            "  - handoff:\n" +
            "      agent: LTAI-Chat\n" +
            "      input: \"{{{input}}}\"\n" +
            "  - handoff:\n" +
            "      agent: LTAI-Code\n" +
            "      input: \"{{{input}}}\"\n";
    }

    private static string GenerateDecisionTreeTemplate(string name)
    {
        return
            "kind: decision-tree\n" +
           $"name: {name}\n" +
            "version: 1\n" +
            "topK: 3\n" +
            "confidenceMarginThreshold: 0.15\n" +
            "minTopScoreThreshold: 0.3\n" +
            "ambiguousFallback: LTAI-Chat\n" +
            "minAcceptableScore: 0.1\n" +
            "candidates: [LTAI-Chat, LTAI-Code, LTAI-Data]\n";
    }
}
