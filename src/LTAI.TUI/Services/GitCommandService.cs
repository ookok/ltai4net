using System.Diagnostics;
using System.Text;
using LTAI.Core.Commands;
using Spectre.Console;

namespace LTAI.TUI.Services;

public sealed class GitCommandService : ICommandService
{
    public CommandResult Execute(Command command) => command switch
    {
        GitCommand gc => HandleGit(gc.Args),
        _ => new SuccessResult("ok"),
    };

    private static CommandResult HandleGit(string args)
    {
        if (string.IsNullOrWhiteSpace(args) || args == "help")
        {
            return new SuccessResult(
                "[bold]Git 命令[/]\n"
                + "  /git status    — 查看工作区状态（含结构化文件变更列表）\n"
                + "  /git diff      — 查看未暂存的变更差异\n"
                + "  /git diff --cached — 查看已暂存的变更\n"
                + "  /git log       — 查看提交历史\n"
                + "  /git add <file> — 暂存文件\n"
                + "  /git commit -m \"msg\" — 提交\n"
                + "  /git pull      — 拉取\n"
                + "  /git push      — 推送\n"
                + "  /git <任意 git 参数> — 直接透传");
        }

        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = new Process { StartInfo = psi };
            p.Start();
            var output = p.StandardOutput.ReadToEnd();
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);

            var sb = new StringBuilder();
            var isOk = p.ExitCode == 0;

            if (args == "status" || (args.StartsWith("status ") && !args.Contains("porcelain")))
            {
                foreach (var line in output.Split('\n'))
                {
                    if (line.Contains("nothing to commit"))
                        sb.AppendLine($"[green]{line.EscapeMarkup()}[/]");
                    else if (line.StartsWith("On branch ") || line.StartsWith("HEAD "))
                        sb.AppendLine($"[blue]{line.EscapeMarkup()}[/]");
                    else if (line.TrimStart().StartsWith("modified:"))
                        sb.AppendLine($"[yellow]{line.EscapeMarkup()}[/]");
                    else if (line.TrimStart().StartsWith("new file:"))
                        sb.AppendLine($"[green]{line.EscapeMarkup()}[/]");
                    else if (line.TrimStart().StartsWith("deleted:"))
                        sb.AppendLine($"[red]{line.EscapeMarkup()}[/]");
                    else if (line.TrimStart().StartsWith("renamed:"))
                        sb.AppendLine($"[yellow]{line.EscapeMarkup()}[/]");
                    else if (string.IsNullOrWhiteSpace(line))
                        sb.AppendLine();
                    else
                        sb.AppendLine($"[white]{line.EscapeMarkup()}[/]");
                }
            }
            else if (args == "diff")
            {
                var inHunk = false;
                foreach (var line in output.Split('\n'))
                {
                    if (line.StartsWith("diff --git"))
                    {
                        if (inHunk) sb.AppendLine();
                        sb.AppendLine($"[bold cyan]{line.EscapeMarkup()}[/]");
                        inHunk = false;
                    }
                    else if (line.StartsWith("--- ") || line.StartsWith("+++ ") || line.StartsWith("index "))
                        sb.AppendLine($"[bold]{line.EscapeMarkup()}[/]");
                    else if (line.StartsWith("@@"))
                        sb.AppendLine($"[blue]{line.EscapeMarkup()}[/]");
                    else if (line.StartsWith("+") && !line.StartsWith("+++"))
                        sb.AppendLine($"[green]{line.EscapeMarkup()}[/]");
                    else if (line.StartsWith("-") && !line.StartsWith("---"))
                        sb.AppendLine($"[red]{line.EscapeMarkup()}[/]");
                    else
                        sb.AppendLine($"[grey]{line.EscapeMarkup()}[/]");
                    inHunk = true;
                }
            }
            else
            {
                foreach (var line in output.Split('\n'))
                {
                    if (line.Contains("error:") || line.Contains("fatal:"))
                        sb.AppendLine($"[red]{line.EscapeMarkup()}[/]");
                    else
                        sb.AppendLine($"[white]{line.EscapeMarkup()}[/]");
                }
            }

            if (!string.IsNullOrEmpty(error))
                sb.AppendLine($"[red]{error.EscapeMarkup()}[/]");

            var header = isOk ? "[green]✅ git[/]" : "[red]❌ git[/]";
            return new SuccessResult($"{header}\n{sb.ToString().TrimEnd()}");
        }
        catch (Exception ex)
        {
            return new SuccessResult($"[red]git 错误: {ex.Message}[/]");
        }
    }
}
