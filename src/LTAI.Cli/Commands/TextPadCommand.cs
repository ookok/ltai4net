using System.Text;
using Spectre.Console;

namespace LTAI.Cli;

partial class Program
{
    internal static int HandleTextPad(string[] args)
    {
        var path = args.Length > 0 ? args[0] : ".";
        var root = Path.GetFullPath(path);
        var workspace = Path.GetFullPath(Directory.GetCurrentDirectory());
        if (!root.StartsWith(workspace + Path.DirectorySeparatorChar) &&
            root != workspace)
        { Error("Path must be within the workspace directory"); return 1; }
        if (!Directory.Exists(root) && !File.Exists(root)) { Error($"路径不存在: {root}"); return 1; }

        if (File.Exists(root))
        {
            var fi = new FileInfo(root);
            if (fi.Length > 10_485_760) { Error("File exceeds 10MB limit"); return 1; }
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

            // Extract filename only — skip ANSI markup prefix tokens
            var name = choice;
            var lastSpace = name.LastIndexOf(' ');
            if (lastSpace >= 0) name = name[(lastSpace + 1)..];
            name = name.Replace("/", "").Trim();
            var clean = Path.GetFullPath(Path.Combine(currentDir, name));
            if (Directory.Exists(clean)) { currentDir = clean; continue; }
            if (!File.Exists(clean)) break;

            var ext = Path.GetExtension(clean).ToLowerInvariant();
            if (ext is ".md" or ".txt")
            {
                var fi = new FileInfo(clean);
                if (fi.Length > 5_242_880) { AnsiConsole.MarkupLine("[yellow]文件超过 5MB，仅显示前 5MB[/]"); }
                AnsiConsole.Write(new Panel(File.ReadAllText(clean).EscapeMarkup())
                    .Header($"[bold]{Path.GetFileName(clean)}[/]").BorderColor(Color.Blue).Expand());
            }
            else
            {
                var sb = new StringBuilder();
                var lineCount = 0;
                foreach (var line in File.ReadLines(clean))
                {
                    if (lineCount >= 200) break;
                    sb.AppendLine($"[grey]{lineCount + 1,4}[/] {line.EscapeMarkup()}");
                    lineCount++;
                }
                AnsiConsole.Write(new Panel(sb.ToString().TrimEnd())
                    .Header($"[bold]{Path.GetFileName(clean)}[/]").BorderColor(Color.Green).Expand());
            }
            AnsiConsole.MarkupLine("[grey]按任意键继续...[/]");
            System.Console.ReadKey(true);
        }
        return 0;
    }
}
