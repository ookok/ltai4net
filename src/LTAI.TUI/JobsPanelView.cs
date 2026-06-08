using LTAI.Agent.Tasks;
using Spectre.Console;
using TaskState = LTAI.Agent.Tasks.TaskStatus;

namespace LTAI.TUI;

public sealed class JobsPanelView
{
    private readonly TaskQueue? _queue;

    public JobsPanelView(TaskQueue? queue = null)
    {
        _queue = queue;
    }

    public void Render()
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule("[bold]📋 后台作业[/]") { Style = Style.Parse("bold") });
            AnsiConsole.MarkupLine("[dim]命令: list, cancel <id>, refresh, q=返回[/]\n");

            var items = _queue?.List() ?? [];
            if (items.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]暂无后台作业[/]");
            }
            else
            {
                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("[bold]ID[/]");
                table.AddColumn("[bold]名称[/]");
                table.AddColumn("[bold]状态[/]");
                table.AddColumn("[bold]尝试[/]");
                table.AddColumn("[bold]创建时间[/]");

                foreach (var item in items)
                {
                    var statusColor = item.Status switch
                    {
                        TaskState.Running => "yellow",
                        TaskState.Completed => "green",
                        TaskState.Failed => "red",
                        TaskState.Pending => "grey",
                        TaskState.Cancelled => "silver",
                        _ => "grey"
                    };
                    var statusLabel = item.Status switch
                    {
                        TaskState.Running => "运行中",
                        TaskState.Completed => "已完成",
                        TaskState.Failed => "失败",
                        TaskState.Pending => "等待中",
                        TaskState.Cancelled => "已取消",
                        _ => item.Status.ToString()
                    };
                    table.AddRow(
                        item.Id[..8].EscapeMarkup(),
                        item.Name.EscapeMarkup(),
                        $"[{statusColor}]{statusLabel}[/]",
                        item.Attempt.ToString(),
                        item.EnqueuedAt.ToLocalTime().ToString("HH:mm:ss"));
                }
                AnsiConsole.Write(table);

                var stats = _queue is not null
                    ? $"[grey]总计 {items.Count} | 已完成 {_queue.CompletedCount} | 失败 {_queue.FailedCount} | 已取消 {_queue.CancelledCount}[/]"
                    : "";
                if (!string.IsNullOrEmpty(stats))
                    AnsiConsole.MarkupLine(stats);
            }

            var input = AnsiConsole.Ask<string>("\n[grey]>[/] ").Trim();
            if (input is "q" or "quit" or "exit") break;

            var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToLowerInvariant();
            var arg = parts.Length > 1 ? parts[1] : null;

            switch (cmd)
            {
                case "list":
                case "refresh":
                    continue;
                case "cancel" when arg != null:
                    CancelTask(arg);
                    break;
                default:
                    AnsiConsole.MarkupLine($"[red]未知命令: {cmd.EscapeMarkup()}[/]");
                    break;
            }
            if (cmd != "refresh" && cmd != "list")
            {
                AnsiConsole.MarkupLine("[grey]按任意键继续...[/]");
                Console.ReadKey(true);
            }
        }
    }

    private void CancelTask(string idOrPrefix)
    {
        if (_queue is null) return;
        var items = _queue.List();
        var match = items.FirstOrDefault(i => i.Id == idOrPrefix)
                    ?? items.FirstOrDefault(i => i.Id.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            AnsiConsole.MarkupLine($"[red]未找到任务: {idOrPrefix.EscapeMarkup()}[/]");
            return;
        }
        if (match.Status is TaskState.Completed or TaskState.Failed or TaskState.Cancelled)
        {
            AnsiConsole.MarkupLine($"[yellow]任务 {match.Id[..8].EscapeMarkup()} 已结束 (状态: {match.Status})[/]");
            return;
        }
        match.Status = TaskState.Cancelled;
        match.Error = "Cancelled by user via TUI";
        AnsiConsole.MarkupLine($"[yellow]已取消任务: {match.Id[..8].EscapeMarkup()} ({match.Name.EscapeMarkup()})[/]");
    }
}
