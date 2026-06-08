using System.Globalization;
using Spectre.Console;
using LTAI.Core.Session;

namespace LTAI.TUI;

public sealed class SessionsPanelView
{
    private readonly SessionManager _sessions;

    public SessionsPanelView(SessionManager sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public void Render()
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule("[bold]📋 会话管理[/]") { Style = Style.Parse("bold") });
            AnsiConsole.MarkupLine("[dim]命令: list, load <name>, delete <name>, new, search <text> | q=返回[/]\n");

            var infos = _sessions.ListSessions();
            if (infos.Length == 0)
            {
                AnsiConsole.MarkupLine("[grey]暂无会话[/]");
            }
            else
            {
                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("[bold]#[/]");
                table.AddColumn("[bold]名称[/]");
                table.AddColumn(new TableColumn("[bold]消息[/]").RightAligned());
                table.AddColumn("[bold]创建时间[/]");

                for (int i = 0; i < infos.Length; i++)
                {
                    var s = infos[i];
                    var name = s.Name.EscapeMarkup();
                    var isCurrent = s.Name == _sessions.CurrentSession;
                    if (isCurrent)
                        name = $"[green]{name} ←当前[/]";

                    var created = ParseTimestampFromName(s.Name);
                    var detail = !string.IsNullOrEmpty(s.ParentId)
                        ? $"[blue]子会话[/]"
                        : "-";

                    table.AddRow(
                        $"{i + 1}",
                        name,
                        detail,
                        created);
                }
                AnsiConsole.Write(table);
            }

            AnsiConsole.Markup("\n[grey]>[/] ");
            var input = Console.ReadLine()?.Trim() ?? "";
            if (input is "q" or "quit" or "exit") break;

            var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToLowerInvariant();
            var arg = parts.Length > 1 ? parts[1] : null;

            switch (cmd)
            {
                case "list":
                    continue;
                case "new":
                    var handle = _sessions.NewSession();
                    AnsiConsole.MarkupLine($"[green]已创建新会话: {handle.Name.EscapeMarkup()}[/]");
                    break;
                case "load" when arg != null:
                    var loaded = _sessions.LoadSession(arg);
                    if (loaded != null)
                        AnsiConsole.MarkupLine($"[green]已加载会话: {arg.EscapeMarkup()}[/]");
                    else
                        AnsiConsole.MarkupLine($"[red]未找到会话: {arg.EscapeMarkup()}[/]");
                    break;
                case "delete" when arg != null:
                    _sessions.DeleteSession(arg);
                    AnsiConsole.MarkupLine($"[yellow]已删除会话: {arg.EscapeMarkup()}[/]");
                    break;
                case "search" when arg != null:
                    var matching = infos.Where(s => s.Name.Contains(arg, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matching.Length == 0)
                        AnsiConsole.MarkupLine($"[yellow]未找到匹配: {arg.EscapeMarkup()}[/]");
                    else
                        AnsiConsole.MarkupLine($"[green]找到 {matching.Length} 个匹配: {string.Join(", ", matching.Select(s => s.Name.EscapeMarkup()))}[/]");
                    break;
                default:
                    AnsiConsole.MarkupLine($"[red]未知命令: {cmd.EscapeMarkup()}[/]");
                    break;
            }

            AnsiConsole.MarkupLine("[grey]按任意键继续...[/]");
            Console.ReadKey(true);
        }
    }

    private static string ParseTimestampFromName(string rawName)
    {
        if (rawName.StartsWith("session-") && rawName.Length >= 22)
        {
            if (DateTime.TryParseExact(
                    rawName[8..22],
                    "yyyyMMdd-HHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var dt))
                return dt.ToString("yyyy-MM-dd HH:mm");
        }
        return "-";
    }
}
