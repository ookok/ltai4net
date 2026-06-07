using Spectre.Console;

namespace LTAI.Cli;

partial class Program
{
    internal static int HandleTextPad(string[] args)
    {
        var path = args.Length > 0 ? args[0] : ".";
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root) && !File.Exists(root)) { Error($"路径不存在: {root}"); return 1; }

        if (File.Exists(root))
        {
            var content = File.ReadAllText(root);
            AnsiConsole.Write(new Panel(content.EscapeMarkup())
                .Header($"[bold]{Path.GetFileName(root)}[/]").BorderColor(Color.Green).Expand());
            return 0;
        }

        var running = true;
        var currentDir = root;
        while (running)
        {
            Console.Clear();
            AnsiConsole.MarkupLine($"[bold]文件浏览器[/] — [grey]{currentDir.EscapeMarkup()}[/]");
            var items = new List<string>();
            try
            {
                items.AddRange(Directory.GetDirectories(currentDir).Select(d => $"[cyan]📁 {Path.GetFileName(d)}/[/]"));
                items.AddRange(Directory.GetFiles(currentDir).Select(f => $"[grey]📄 {Path.GetFileName(f)}[/]"));
            }
            catch { AnsiConsole.MarkupLine("[red]无法读取目录[/]"); break; }
            if (items.Count == 0) { AnsiConsole.MarkupLine("[grey](空目录)[/]"); break; }

            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("[yellow]选择:[/]").PageSize(20).AddChoices(items));
            if (string.IsNullOrEmpty(choice)) break;

            var clean = Path.GetFullPath(Path.Combine(currentDir,
                choice.Replace("📁 ", "").Replace("📄 ", "").Replace("[cyan]", "").Replace("[/]", "").Replace("[grey]", "")));
            if (Directory.Exists(clean)) { currentDir = clean; continue; }
            if (!File.Exists(clean)) break;

            var ext = Path.GetExtension(clean).ToLowerInvariant();
            if (ext is ".md" or ".txt")
            {
                AnsiConsole.Write(new Panel(File.ReadAllText(clean).EscapeMarkup())
                    .Header($"[bold]{Path.GetFileName(clean)}[/]").BorderColor(Color.Blue).Expand());
            }
            else
            {
                var lines = File.ReadAllLines(clean);
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < Math.Min(lines.Length, 200); i++)
                    sb.AppendLine($"[grey]{i + 1,4}[/] {lines[i].EscapeMarkup()}");
                if (lines.Length > 200) sb.AppendLine($"[grey]... 仅显示前 200 行，共 {lines.Length} 行[/]");
                AnsiConsole.Write(new Panel(sb.ToString().TrimEnd())
                    .Header($"[bold]{Path.GetFileName(clean)}[/]").BorderColor(Color.Green).Expand());
            }
            AnsiConsole.MarkupLine("[grey]按任意键继续...[/]");
            System.Console.ReadKey(true);
        }
        return 0;
    }
}
