using System.Text;
using LTAI.Agent.Tools;
using LTAI.Core.Commands;
using Spectre.Console;

namespace LTAI.TUI.Services;

public sealed class JobsCommandService : ICommandService
{
    private readonly BackgroundJobService? _jobs;

    public JobsCommandService(BackgroundJobService? jobs)
    {
        _jobs = jobs;
    }

    public CommandResult Execute(Command command) => command switch
    {
        JobsCommand jc => HandleJobsCommand(jc.Args),
        _ => new SuccessResult("ok"),
    };

    private CommandResult HandleJobsCommand(string args)
    {
        var jobs = _jobs;
        if (jobs == null)
            return new SuccessResult("Background job service not initialized (BGJS missing in DI)");

        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var subArgs = parts.Length > 1 ? parts[1].Trim() : "";

        return sub switch
        {
            "" or "list" => JobsList(jobs),
            "watch" => JobsWatch(jobs, subArgs),
            "cancel" => JobsCancel(jobs, subArgs),
            "show" => JobsShow(jobs, subArgs),
            _ => new SuccessResult("用法: /jobs list | watch <id> | cancel <id> | show <id>"),
        };
    }

    private static CommandResult JobsList(BackgroundJobService jobs)
    {
        var snap = jobs.SnapshotJobs();
        if (snap.Count == 0)
            return new SuccessResult("[yellow]暂无后台作业[/]  用法: 让 agent 跑 `start_job` 创建");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("ID");
        table.AddColumn("状态");
        table.AddColumn("Exit");
        table.AddColumn("已运行");
        table.AddColumn("命令");

        foreach (var (id, j) in snap.OrderBy(kv => int.TryParse(kv.Key, out var n) ? n : 0))
        {
            string statusIcon, statusColor;
            if (!j.Completed) { statusIcon = "⏳"; statusColor = "yellow"; }
            else if (j.ExitCode == 0) { statusIcon = "✅"; statusColor = "green"; }
            else if (j.Error == "Cancelled") { statusIcon = "🚫"; statusColor = "grey"; }
            else { statusIcon = "❌"; statusColor = "red"; }

            var elapsed = DateTime.UtcNow - j.StartedAtUtc;
            var elapsedStr = elapsed.TotalSeconds < 60
                ? $"{(int)elapsed.TotalSeconds}s"
                : $"{elapsed.Minutes}m{elapsed.Seconds}s";

            var cmd = j.Command ?? "";
            if (cmd.Length > 60) cmd = cmd[..57] + "...";

            table.AddRow(
                $"[cyan]{id.EscapeMarkup()}[/]",
                $"[{statusColor}]{statusIcon} {(j.Completed ? (j.ExitCode == 0 ? "完成" : j.Error == "Cancelled" ? "取消" : "失败") : "运行中")}[/]",
                j.Completed ? (j.ExitCode?.ToString() ?? "?") : "[grey]-[/]",
                elapsedStr,
                $"[grey]{cmd.EscapeMarkup()}[/]");
        }

        AnsiConsole.Write(table);
        return new SuccessResult($"[grey]共 {snap.Count} 个作业 (60s 后自动清理)[/]");
    }

    private static CommandResult JobsWatch(BackgroundJobService jobs, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return new SuccessResult("用法: /jobs watch <id>  例如: /jobs watch 3");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timeout = TimeSpan.FromMinutes(2);
        JobEntry? lastEntry = null;

        AnsiConsole.MarkupLine($"[grey]Watching job #{id}... (Ctrl+C 退出, 最多 2 分钟)[/]");
        while (sw.Elapsed < timeout)
        {
            var entry = jobs.GetJobEntry(id);
            if (entry == null)
                return new SuccessResult($"[yellow]⚠ Job #{id} 已被清理（60s 过期或不存在）[/]");

            if (entry != lastEntry)
            {
                var status = !entry.Completed ? "[yellow]⏳ 运行中[/]"
                    : entry.ExitCode == 0 ? "[green]✅ 完成[/]"
                    : entry.Error == "Cancelled" ? "[grey]🚫 取消[/]"
                    : "[red]❌ 失败[/]";
                var elapsed = DateTime.UtcNow - entry.StartedAtUtc;
                AnsiConsole.MarkupLine($"  [{DateTime.Now:HH:mm:ss}] {status}  ({elapsed.TotalSeconds:F0}s)");
                lastEntry = entry;
            }

            if (entry.Completed)
            {
                AnsiConsole.WriteLine();
                return JobsShow(jobs, id);
            }

            System.Threading.Thread.Sleep(100);
        }

        return new SuccessResult($"[yellow]⏱ 2 分钟超时，job #{id} 仍在运行。退出 watch（job 仍存在）[/]");
    }

    private static CommandResult JobsCancel(BackgroundJobService jobs, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return new SuccessResult("用法: /jobs cancel <id>");

        var entry = jobs.GetJobEntry(id);
        if (entry == null)
            return new SuccessResult($"[yellow]⚠ Job #{id} 不存在（可能已完成并被清理）[/]");

        if (entry.Completed)
            return new SuccessResult($"[grey]Job #{id} 已完成 (exit={entry.ExitCode}), 无需取消[/]");

        entry.Completed = true;
        entry.Error = "Cancelled";
        return new SuccessResult($"[green]✅ 已标记取消[/] Job #{id} (BGJS 不杀进程, residual 退出后自然消失)");
    }

    private static CommandResult JobsShow(BackgroundJobService jobs, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return new SuccessResult("用法: /jobs show <id>");

        var entry = jobs.GetJobEntry(id);
        if (entry == null)
            return new SuccessResult($"[yellow]⚠ Job #{id} 不存在（可能已完成并被清理）[/]");

        var elapsed = DateTime.UtcNow - entry.StartedAtUtc;
        var sb = new StringBuilder();
        sb.AppendLine($"[bold]Job #{id}[/]");
        sb.AppendLine($"  状态: {(entry.Completed ? (entry.ExitCode == 0 ? "[green]✅ 完成[/]" : entry.Error == "Cancelled" ? "[grey]🚫 取消[/]" : "[red]❌ 失败[/]") : "[yellow]⏳ 运行中[/]")}");
        sb.AppendLine($"  命令: [grey]{(entry.Command ?? "").EscapeMarkup()}[/]");
        sb.AppendLine($"  启动: {entry.StartedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"  已运行: {elapsed.TotalSeconds:F0}s");
        if (entry.Completed) sb.AppendLine($"  Exit: {entry.ExitCode?.ToString() ?? "?"}");
        sb.AppendLine($"  stdout: {entry.Output?.Length ?? 0} bytes");
        sb.AppendLine($"  stderr: {entry.Error?.Length ?? 0} bytes");

        if (entry.Completed && !string.IsNullOrEmpty(entry.Output))
        {
            var preview = entry.Output.Length > 500
                ? entry.Output[..500] + $"\n... ({entry.Output.Length - 500} more bytes)"
                : entry.Output;
            sb.AppendLine();
            sb.AppendLine("  [grey]── stdout (前 500 字符) ──[/]");
            sb.AppendLine("  " + preview.Replace("\n", "\n  ").EscapeMarkup());
        }
        if (entry.Completed && !string.IsNullOrEmpty(entry.Error) && entry.Error != "Cancelled")
        {
            var preview = entry.Error.Length > 500
                ? entry.Error[..500] + $"\n... ({entry.Error.Length - 500} more bytes)"
                : entry.Error;
            sb.AppendLine();
            sb.AppendLine("  [red]── stderr (前 500 字符) ──[/]");
            sb.AppendLine("  " + preview.Replace("\n", "\n  ").EscapeMarkup());
        }

        return new SuccessResult(sb.ToString());
    }
}
