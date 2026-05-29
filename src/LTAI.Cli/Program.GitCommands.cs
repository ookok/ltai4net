using System.Text.Json;
using Spectre.Console;

namespace LTAI.Cli;

partial class Program
{
    // ════════════════════════════════════════════════════════════════
    // ltai git
    // ════════════════════════════════════════════════════════════════

    private static async Task RunGitAsync(string[] args)
    {
        var sub = args.FirstOrDefault()?.ToLowerInvariant();
        var rest = args.Length > 1 ? args[1..] : [];

        try
        {
            switch (sub)
            {
                case "status":  await RunGitStatusAsync(rest); break;
                case "log":     await RunGitLogAsync(rest); break;
                case "diff":    await RunGitDiffAsync(rest); break;
                case "commit":  await RunGitCommitAsync(rest); break;
                case "branch":  await RunGitBranchAsync(rest); break;
                case "checkout": await RunGitCheckoutAsync(rest); break;
                case "pull":    await RunGitPullAsync(rest); break;
                case "stash":   await RunGitStashAsync(rest); break;
                case "tag":     await RunGitTagAsync(rest); break;
                case "show":    await RunGitShowAsync(rest); break;
                case "blame":   await RunGitBlameAsync(rest); break;
                case "remote":  await RunGitRemoteAsync(rest); break;
                case "reset":   await RunGitResetAsync(rest); break;
                case "clone":   await RunGitCloneAsync(rest); break;
                case null or "" or "help": PrintGitHelp(); break;
                default:
                    AnsiConsole.MarkupLine($"[red]Unknown git subcommand: '{sub}'[/]");
                    AnsiConsole.MarkupLine("[dim]Run 'ltai git help' for available commands.[/]");
                    break;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Git error: {ex.Message}[/]");
        }
    }

    private static void PrintGitHelp()
    {
        AnsiConsole.MarkupLine("[bold cyan]ltai git[/] — native Git operations (no git CLI required, powered by libgit2sharp)");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[bold]Subcommands:[/]");
        AnsiConsole.MarkupLine("  [bold]status[/]                  Show working tree status");
        AnsiConsole.MarkupLine("  [bold]log[/] [-n N]              Show commit history");
        AnsiConsole.MarkupLine("  [bold]diff[/] [--staged] [files]  Show working tree changes");
        AnsiConsole.MarkupLine("  [bold]commit[/] -m <msg>          Create a commit");
        AnsiConsole.MarkupLine("  [bold]branch[/] [create|delete] <name>  Manage branches");
        AnsiConsole.MarkupLine("  [bold]checkout[/] <branch|file>   Switch branch or restore file");
        AnsiConsole.MarkupLine("  [bold]pull[/] [remote] [branch]   Fetch and merge");
        AnsiConsole.MarkupLine("  [bold]stash[/] [push|pop|list]    Manage stashes");
        AnsiConsole.MarkupLine("  [bold]tag[/] [create|list|delete]  Manage tags");
        AnsiConsole.MarkupLine("  [bold]show[/] <commit>            Show commit details and diff");
        AnsiConsole.MarkupLine("  [bold]blame[/] <file>             Show line-by-line attribution");
        AnsiConsole.MarkupLine("  [bold]remote[/] [list|show|add]   Manage remotes");
        AnsiConsole.MarkupLine("  [bold]reset[/] [--soft|--mixed|--hard] [target]  Reset HEAD");
        AnsiConsole.MarkupLine("  [bold]clone[/] <url> [--branch B] [--shallow]  Clone a repository");
    }

    private static async Task RunGitStatusAsync(string[] args)
    {
        var json = await LTAI.Agent.Tools.GitTools.GitStatus();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        { AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]"); return; }

        var branch = root.GetProperty("branch").GetString();
        var dirty = root.GetProperty("isDirty").GetBoolean();
        AnsiConsole.MarkupLine($"[bold]Branch:[/] [cyan]{branch}[/] {(dirty ? "[yellow](dirty)[/]" : "[green](clean)[/]")}");
        PrintFileList("Staged", root, "staged", Color.Green);
        PrintFileList("Modified", root, "modified", Color.Yellow);
        PrintFileList("Added", root, "added", Color.Green);
        PrintFileList("Deleted", root, "removed", Color.Red);
        PrintFileList("Untracked", root, "untracked", Color.Grey);
        PrintFileList("Renamed", root, "renamed", Color.Cyan1);
    }

    private static async Task RunGitLogAsync(string[] args)
    {
        var maxCount = 20;
        var nIdx = Array.IndexOf(args, "-n");
        if (nIdx >= 0 && nIdx + 1 < args.Length && int.TryParse(args[nIdx + 1], out var n))
            maxCount = n;

        var json = await LTAI.Agent.Tools.GitTools.GitLog(maxCount: maxCount);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        { AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]"); return; }

        AnsiConsole.MarkupLine($"[bold]Branch:[/] [cyan]{root.GetProperty("currentBranch").GetString()}[/]\n");
        foreach (var commit in root.GetProperty("commits").EnumerateArray())
            AnsiConsole.MarkupLine($"[yellow]{commit.GetProperty("hash").GetString()}[/] [dim]{commit.GetProperty("date").GetString()}[/] [cyan]{commit.GetProperty("author").GetString()}[/]\n  {commit.GetProperty("message").GetString()}");
    }

    private static async Task RunGitDiffAsync(string[] args)
    {
        var staged = args.Contains("--staged") || args.Contains("-s");
        var files = string.Join(" ", args.Where(a => !a.StartsWith("-")));
        var json = await LTAI.Agent.Tools.GitTools.GitDiff(staged: staged, files: string.IsNullOrWhiteSpace(files) ? null : files);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        { AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]"); return; }

        AnsiConsole.MarkupLine($"[bold]{root.GetProperty("count").GetInt32()} file(s) changed[/] {(staged ? "[dim](staged)[/]" : "")}");
        foreach (var change in root.GetProperty("changes").EnumerateArray())
        {
            var color = change.GetProperty("status").GetString() switch
            {
                "Added" or "NewInIndex" => "green",
                "Modified" or "ModifiedInIndex" => "yellow",
                "Deleted" or "RemovedFromIndex" => "red",
                "RenamedInIndex" => "cyan",
                _ => "grey"
            };
            AnsiConsole.MarkupLine($"  [{color}]{change.GetProperty("status").GetString()}[/] {change.GetProperty("path").GetString()}");
        }

        if (root.TryGetProperty("patch", out var patch) && patch.ValueKind == JsonValueKind.String)
        {
            AnsiConsole.MarkupLine("\n[bold]Diff:[/]");
            AnsiConsole.MarkupLine(patch.GetString()!);
        }
    }

    private static async Task RunGitCommitAsync(string[] args)
    {
        var msgIdx = Array.IndexOf(args, "-m");
        if (msgIdx < 0 || msgIdx + 1 >= args.Length)
        { AnsiConsole.MarkupLine("[red]Usage: ltai git commit -m \"message\"[/]"); return; }

        var message = args[msgIdx + 1];
        var json = await LTAI.Agent.Tools.GitTools.GitCommit(message, stageAll: true);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        { AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]"); return; }

        AnsiConsole.MarkupLine($"[green]Committed:[/] [yellow]{root.GetProperty("sha").GetString()}[/] — {message}");
    }

    private static async Task RunGitBranchAsync(string[] args)
    {
        var op = args.FirstOrDefault() ?? "list";
        string? name = args.Length > 1 ? args[1] : null;

        if (op is "create" or "delete" && string.IsNullOrWhiteSpace(name))
        { AnsiConsole.MarkupLine($"[red]Usage: ltai git branch {op} <name>[/]"); return; }

        var json = await LTAI.Agent.Tools.GitTools.GitBranch(operation: op, name: name);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        { AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]"); return; }

        if (op == "create") { AnsiConsole.MarkupLine($"[green]Branch created:[/] [cyan]{root.GetProperty("created").GetString()}[/]"); return; }
        if (op == "delete") { AnsiConsole.MarkupLine($"[green]Branch deleted:[/] [cyan]{root.GetProperty("deleted").GetString()}[/]"); return; }

        foreach (var b in root.GetProperty("branches").EnumerateArray())
        {
            var bName = b.GetProperty("name").GetString();
            var isCur = b.GetProperty("isCurrent").GetBoolean();
            var isRemote = b.GetProperty("isRemote").GetBoolean();
            AnsiConsole.MarkupLine($"{(isCur ? "[green]*[/]" : " ")} [{(isCur ? "bold cyan" : isRemote ? "dim" : "")}]{bName}[/]");
        }
    }

    private static async Task RunGitCheckoutAsync(string[] args)
    {
        var target = args.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(target))
        { AnsiConsole.MarkupLine("[red]Usage: ltai git checkout <branch|file>[/]"); return; }

        var json = await LTAI.Agent.Tools.GitTools.GitCheckout(target);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        { AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]"); return; }

        AnsiConsole.MarkupLine($"[green]Checked out {root.GetProperty("type").GetString()}:[/] [cyan]{target}[/]");
    }

    private static async Task RunGitPullAsync(string[] args)
    {
        var remote = args.FirstOrDefault() ?? "origin";
        var branch = args.Length > 1 ? args[1] : null;
        var json = await LTAI.Agent.Tools.GitTools.GitPull(remote: remote, branch: branch);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        { AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]"); return; }

        AnsiConsole.MarkupLine(root.GetProperty("merged").GetBoolean()
            ? $"[green]Pulled and merged:[/] {root.GetProperty("status").GetString()}"
            : $"[dim]Fetched: up to date[/]");
    }

    private static async Task RunGitStashAsync(string[] args)
    {
        var op = args.FirstOrDefault() ?? "push";
        var message = op == "push" && args.Length > 1 ? args[1] : null;
        var json = await LTAI.Agent.Tools.GitTools.GitStash(operation: op, message: message);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        { AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]"); return; }

        if (root.TryGetProperty("stashed", out _)) AnsiConsole.MarkupLine("[green]Stashed changes[/]");
        else if (root.TryGetProperty("popped", out _)) AnsiConsole.MarkupLine("[green]Popped stash[/]");
        else if (root.TryGetProperty("applied", out _)) AnsiConsole.MarkupLine("[green]Applied stash[/]");
        else if (root.TryGetProperty("dropped", out _)) AnsiConsole.MarkupLine("[green]Dropped stash[/]");
        else if (root.TryGetProperty("stashes", out var stashes))
        {
            AnsiConsole.MarkupLine($"[bold]{stashes.GetArrayLength()} stash(es)[/]");
            foreach (var s in stashes.EnumerateArray())
                AnsiConsole.MarkupLine($"  [{s.GetProperty("index").GetInt32()}] {s.GetProperty("message").GetString()}");
        }
    }

    private static async Task RunGitTagAsync(string[] args)
    {
        var op = args.FirstOrDefault() ?? "list";
        string? name = args.Length > 1 ? args[1] : null;
        string? message = op == "create" && args.Length > 2 ? args[2] : null;
        var json = await LTAI.Agent.Tools.GitTools.GitTag(operation: op, name: name, message: message);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        { AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]"); return; }

        if (root.TryGetProperty("created", out _)) AnsiConsole.MarkupLine($"[green]Tag created:[/] [cyan]{name}[/]");
        else if (root.TryGetProperty("deleted", out _)) AnsiConsole.MarkupLine($"[green]Tag deleted:[/] [cyan]{name}[/]");
        else if (root.TryGetProperty("tags", out var tags))
        {
            AnsiConsole.MarkupLine($"[bold]{tags.GetArrayLength()} tag(s)[/]");
            foreach (var t in tags.EnumerateArray())
                AnsiConsole.MarkupLine($"  [cyan]{t.GetProperty("name").GetString()}[/] [dim]{t.GetProperty("sha").GetString()}[/]");
        }
    }

    private static async Task RunGitShowAsync(string[] args)
    {
        var target = args.FirstOrDefault() ?? "HEAD";
        var json = await LTAI.Agent.Tools.GitTools.GitShow(target: target);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        { AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]"); return; }

        var sha = root.GetProperty("sha").GetString();
        var author = root.GetProperty("author");
        AnsiConsole.MarkupLine($"[bold]Commit:[/] [yellow]{sha}[/]");
        AnsiConsole.MarkupLine($"[bold]Author:[/] {author.GetProperty("name").GetString()} [dim]<{author.GetProperty("email").GetString()}>[/]");
        AnsiConsole.MarkupLine($"[bold]Date:[/]   {author.GetProperty("date").GetString()}\n");
        AnsiConsole.MarkupLine($"{root.GetProperty("message").GetString().Trim()}\n");
        AnsiConsole.MarkupLine($"[dim]{root.GetProperty("filesChanged").GetInt32()} file(s) changed[/]");

        if (root.TryGetProperty("diff", out var diff) && diff.ValueKind == JsonValueKind.String)
        { AnsiConsole.MarkupLine("\n[bold]Diff:[/]"); AnsiConsole.MarkupLine(diff.GetString()!); }
    }

    private static async Task RunGitBlameAsync(string[] args)
    {
        var filePath = args.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(filePath))
        { AnsiConsole.MarkupLine("[red]Usage: ltai git blame <file>[/]"); return; }

        var json = await LTAI.Agent.Tools.GitTools.GitBlame(filePath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        { AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]"); return; }

        AnsiConsole.MarkupLine($"[bold]Blame:[/] [cyan]{filePath}[/]\n");
        foreach (var hunk in root.GetProperty("blame").EnumerateArray())
            AnsiConsole.MarkupLine($"[dim]{hunk.GetProperty("lineNumber").GetInt32(),4}[/] [yellow]{hunk.GetProperty("commitSha").GetString()}[/] [cyan]{hunk.GetProperty("author").GetString(),-15}[/] [dim]{hunk.GetProperty("date").GetString()}[/] {hunk.GetProperty("summary").GetString()}");
    }

    private static async Task RunGitRemoteAsync(string[] args)
    {
        var op = args.FirstOrDefault() ?? "list";
        string? name = args.Length > 1 ? args[1] : null;
        string? url = op == "add" && args.Length > 2 ? args[2] : null;
        var json = await LTAI.Agent.Tools.GitTools.GitRemote(operation: op, name: name, url: url);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        { AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]"); return; }

        if (root.TryGetProperty("added", out _))
            AnsiConsole.MarkupLine($"[green]Remote added:[/] [cyan]{name}[/] → {url}");
        else if (root.TryGetProperty("name", out _))
        {
            AnsiConsole.MarkupLine($"[bold]Remote:[/] [cyan]{root.GetProperty("name").GetString()}[/]");
            AnsiConsole.MarkupLine($"  URL:      {root.GetProperty("url").GetString()}");
            AnsiConsole.MarkupLine($"  Push URL: {root.GetProperty("pushUrl").GetString()}");
        }
        else if (root.TryGetProperty("remotes", out var remotes))
        {
            AnsiConsole.MarkupLine($"[bold]{remotes.GetArrayLength()} remote(s)[/]");
            foreach (var r in remotes.EnumerateArray())
                AnsiConsole.MarkupLine($"  [cyan]{r.GetProperty("name").GetString()}[/] → {r.GetProperty("url").GetString()}");
        }
    }

    private static async Task RunGitResetAsync(string[] args)
    {
        var mode = "mixed";
        var target = "HEAD";
        string? filePath = null;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--")) mode = arg[2..];
            else if (target == "HEAD" && arg != "HEAD") target = arg;
        }

        if (target.Contains('.') || target.Contains('/') || target.Contains('\\'))
        { filePath = target; target = "HEAD"; }

        var json = await LTAI.Agent.Tools.GitTools.GitReset(mode: mode, target: target, filePath: filePath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        { AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]"); return; }

        AnsiConsole.MarkupLine($"[green]Reset ({mode}):[/] {target}");
    }

    private static async Task RunGitCloneAsync(string[] args)
    {
        var url = args.FirstOrDefault(a => !a.StartsWith("-"));
        if (string.IsNullOrWhiteSpace(url))
        { AnsiConsole.MarkupLine("[red]Usage: ltai git clone <url> [--branch B] [--shallow] [--target <dir>][/]"); return; }

        var branch = (string?)null;
        var bi = Array.IndexOf(args, "--branch");
        if (bi >= 0 && bi + 1 < args.Length) branch = args[bi + 1];
        var shallow = args.Contains("--shallow");
        var ti = Array.IndexOf(args, "--target");
        var targetDir = ti >= 0 && ti + 1 < args.Length ? args[ti + 1] : null;

        AnsiConsole.MarkupLine($"[dim]Cloning {url}...[/]");
        var json = await LTAI.Agent.Tools.GitTools.GitClone(url, targetDir, branch, shallow);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        { AnsiConsole.MarkupLine($"[red]{err.GetString()}[/]"); return; }

        AnsiConsole.MarkupLine($"[green]Cloned:[/] {root.GetProperty("path").GetString()}");
        AnsiConsole.MarkupLine($"  Branch:  [cyan]{root.GetProperty("branch").GetString()}[/]");
        AnsiConsole.MarkupLine($"  Commit:  [yellow]{root.GetProperty("commit").GetString()}[/]");
    }
}
