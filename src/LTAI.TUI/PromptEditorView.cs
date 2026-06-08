using System.Text;
using Spectre.Console;

namespace LTAI.TUI;

public sealed class PromptEditorView
{
    private readonly string _agentsDir;

    public PromptEditorView(string? agentsDir = null)
    {
        _agentsDir = agentsDir ?? Path.Combine(Environment.CurrentDirectory, "agents");
    }

    public void Render()
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule("[bold]📝 Agent Prompt 编辑器[/]") { Style = Style.Parse("bold") });
            AnsiConsole.MarkupLine("[dim]命令: list, show <name>, edit <name>, q=返回[/]\n");

            var files = Directory.GetFiles(_agentsDir, "*.agent.md");
            if (files.Length == 0)
            {
                AnsiConsole.MarkupLine("[grey]未找到 agent 定义文件 (*.agent.md)[/]");
                AnsiConsole.MarkupLine("[grey]按任意键返回...[/]");
                Console.ReadKey(true);
                break;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("[bold]Agent[/]");
            table.AddColumn("[bold]文件[/]");
            table.AddColumn("[bold]大小[/]");
            foreach (var f in files.OrderBy(f => f))
            {
                var name = Path.GetFileNameWithoutExtension(f).Replace(".agent", "");
                var info = new FileInfo(f);
                table.AddRow(
                    $"[cyan]{name.EscapeMarkup()}[/]",
                    $"[grey]{Path.GetFileName(f).EscapeMarkup()}[/]",
                    info.Length > 1024 ? $"{info.Length / 1024}KB" : $"{info.Length}B");
            }
            AnsiConsole.Write(table);

            var input = AnsiConsole.Ask<string>("\n[grey]>[/] ").Trim();
            if (input is "q" or "quit" or "exit") break;

            var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
            var arg = parts.Length > 1 ? parts[1] : "";

            switch (cmd)
            {
                case "list":
                    break;
                case "show" when !string.IsNullOrEmpty(arg):
                    ShowPrompt(arg, files);
                    break;
                case "edit" when !string.IsNullOrEmpty(arg):
                    EditPrompt(arg, files);
                    break;
                case "show":
                case "edit":
                    AnsiConsole.MarkupLine($"[yellow]用法: /{cmd} <agent name>[/]");
                    break;
                default:
                    AnsiConsole.MarkupLine($"[red]未知命令: {cmd.EscapeMarkup()}[/]");
                    break;
            }
        }
    }

    private static void ShowPrompt(string name, string[] files)
    {
        var match = FindAgentFile(name, files);
        if (match == null)
        {
            AnsiConsole.MarkupLine($"[red]未找到 agent: {name.EscapeMarkup()}[/]");
            PromptContinue();
            return;
        }

        var content = File.ReadAllText(match);
        var lines = content.Split('\n');

        // Parse YAML front-matter
        var sb = new StringBuilder();
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            int endIdx = -1;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---") { endIdx = i; break; }
            }
            if (endIdx > 0)
            {
                sb.AppendLine("[bold yellow]YAML Front-Matter:[/]");
                for (int i = 1; i < endIdx; i++)
                    sb.AppendLine($"  [dim]{lines[i].EscapeMarkup()}[/]");
                sb.AppendLine();

                if (endIdx + 1 < lines.Length)
                {
                    sb.AppendLine("[bold green]Prompt:[/]");
                    sb.AppendLine();
                    for (int i = endIdx + 1; i < lines.Length; i++)
                        sb.AppendLine($"  {lines[i].EscapeMarkup()}");
                }
            }
        }
        else
        {
            foreach (var line in lines)
                sb.AppendLine($"  {line.EscapeMarkup()}");
        }

        AnsiConsole.Write(new Panel(new Markup(sb.ToString()))
            .Header($"[bold] {name.EscapeMarkup()} [/]")
            .Border(BoxBorder.Rounded)
            .Expand());
        PromptContinue();
    }

    private static void EditPrompt(string name, string[] files)
    {
        var match = FindAgentFile(name, files);
        if (match == null)
        {
            AnsiConsole.MarkupLine($"[red]未找到 agent: {name.EscapeMarkup()}[/]");
            PromptContinue();
            return;
        }

        var content = File.ReadAllText(match);
        var lines = content.Split('\n');
        AnsiConsole.MarkupLine($"[grey]当前内容 ({lines.Length} 行):[/]\n");
        for (int i = 0; i < lines.Length; i++)
            AnsiConsole.MarkupLine($"[grey]{i + 1,3}:[/] {lines[i].EscapeMarkup()}");

        AnsiConsole.MarkupLine("\n[dim]输入新内容替换 (空行结束, .abort 取消):[/]");
        var newLines = new List<string>();
        while (true)
        {
            var line = Console.ReadLine();
            if (string.IsNullOrEmpty(line)) break;
            if (line == ".abort") { AnsiConsole.MarkupLine("[yellow]已取消[/]"); return; }
            newLines.Add(line);
        }

        if (newLines.Count > 0)
        {
            File.WriteAllText(match, string.Join("\n", newLines) + "\n");
            AnsiConsole.MarkupLine($"[green]✅ 已更新 {name.EscapeMarkup()} ({newLines.Count} 行)[/]");
        }
        PromptContinue();
    }

    private static string? FindAgentFile(string name, string[] files)
    {
        return files.FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f).Replace(".agent", "")
                .Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static void PromptContinue()
    {
        AnsiConsole.MarkupLine("\n[grey]按任意键继续...[/]");
        Console.ReadKey(true);
    }
}
