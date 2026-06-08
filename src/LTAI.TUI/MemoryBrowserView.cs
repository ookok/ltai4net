using Spectre.Console;
using LTAI.Agent.Memory;

namespace LTAI.TUI;

public sealed class MemoryBrowserView
{
    private readonly PalaceStore? _store;

    public MemoryBrowserView(PalaceStore? store = null)
    {
        _store = store;
    }

    public void Render()
    {
        List<PalaceStore.Drawer>? currentList = null;

        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule("[bold]记忆浏览[/]") { Style = Style.Parse("bold") });
            AnsiConsole.MarkupLine("[dim]命令: list, search <query>, delete <index>, stats, q=返回[/]\n");

            if (_store is null)
            {
                AnsiConsole.MarkupLine("[red]PalaceStore 不可用[/]");
                AnsiConsole.MarkupLine("[grey]按任意键返回...[/]");
                Console.ReadKey(true);
                return;
            }

            currentList ??= _store.GetAllDrawers().ToList();

            if (currentList.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]暂无记忆[/]");
            }
            else
            {
                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("[bold]#[/]");
                table.AddColumn("[bold]内容预览[/]");
                table.AddColumn(new TableColumn("[bold]评分[/]").RightAligned());
                table.AddColumn("[bold]时间[/]");
                table.AddColumn("[bold]位置[/]");

                for (int i = 0; i < currentList.Count; i++)
                {
                    var d = currentList[i];
                    var preview = d.Content.Length > 60
                        ? d.Content[..60].ReplaceLineEndings(" ").EscapeMarkup() + "..."
                        : d.Content.ReplaceLineEndings(" ").EscapeMarkup();
                    var ts = DateTimeOffset.FromUnixTimeMilliseconds(d.CreatedAt).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
                    var score = $"{d.Importance:F2}";
                    var location = $"{d.Wing.EscapeMarkup()}/{d.Room.EscapeMarkup()}";

                    table.AddRow($"{i}", preview, score, ts, location);
                }
                AnsiConsole.Write(table);

                var total = _store.Count();
                AnsiConsole.MarkupLine($"[grey]显示 {currentList.Count}/{total} 条[/]");
            }

            var input = AnsiConsole.Ask<string>("\n[grey]>[/] ").Trim();
            if (input is "q" or "quit" or "exit") break;

            var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToLowerInvariant();
            var arg = parts.Length > 1 ? parts[1] : null;

            switch (cmd)
            {
                case "list":
                    currentList = _store.GetAllDrawers().ToList();
                    continue;
                case "search" when arg != null:
                    currentList = _store.GetAllDrawers()
                        .Where(d => d.Content.Contains(arg, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (currentList.Count == 0)
                        AnsiConsole.MarkupLine($"[yellow]未找到匹配: {arg.EscapeMarkup()}[/]");
                    break;
                case "delete" when arg != null:
                    if (int.TryParse(arg, out var idx) && idx >= 0 && idx < currentList.Count)
                    {
                        var target = currentList[idx];
                        var ok = _store.DeleteDrawer(target.Wing, target.Room, target.DrawerId);
                        if (ok)
                        {
                            AnsiConsole.MarkupLine($"[green]已删除记忆 #{idx} ({target.Wing.EscapeMarkup()}/{target.Room.EscapeMarkup()})[/]");
                            currentList = _store.GetAllDrawers().ToList();
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"[red]删除失败 #{idx}[/]");
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]无效索引: {arg.EscapeMarkup()}[/]");
                    }
                    break;
                case "stats":
                    ShowStats();
                    break;
                default:
                    AnsiConsole.MarkupLine($"[red]未知命令: {cmd.EscapeMarkup()}[/]");
                    break;
            }

            AnsiConsole.MarkupLine("[grey]按任意键继续...[/]");
            Console.ReadKey(true);
        }
    }

    private void ShowStats()
    {
        if (_store is null) return;

        var wings = _store.ListWings();
        var total = _store.Count();

        var panel = new Panel(
            Align.Center(new Markup(
                $"[bold]总记忆数:[/] {total}\n" +
                $"[bold]Wings:[/] {string.Join(", ", wings.Select(w => w.EscapeMarkup()))}\n" +
                $"[bold]Wing 数量:[/] {wings.Count}")))
        {
            Header = new PanelHeader("[bold]统计信息[/]"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1, 2, 1),
        };
        AnsiConsole.Write(panel);
    }
}
