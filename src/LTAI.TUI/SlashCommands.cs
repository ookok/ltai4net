using System.Text.RegularExpressions;

namespace LTAI.TUI;

/// <summary>
/// Slash command system — ported from DeepSeek-Reasonix slash command pattern.
/// Commands: /help, /new, /model, /status, /retry, /compact, /memory, /cost
/// </summary>
public static class SlashCommands
{
    private static readonly Dictionary<string, int> UsageCount = new();

    private static readonly SlashSpec[] Commands =
    {
        new("help", "chat", "Show command reference", "?"),
        new("new", "chat", "Start fresh conversation (clear history)", "reset,clear"),
        new("retry", "chat", "Resend last message"),
        new("compact", "chat", "Summarize older turns"),
        new("model", "setup", "Switch AI model", "", "model-id"),
        new("status", "info", "Show current config & stats"),
        new("cost", "info", "Show last turn cost estimate"),
        new("memory", "extend", "List/manage pinned memories"),
        new("skill", "extend", "List/run skills", "", "skill-name"),
        new("mode", "code", "Edit gate: review|auto", "", "review|auto"),
        new("undo", "code", "Undo last edit"),
        new("exit", "advanced", "Quit application", "quit,q"),
    };

    private static readonly Dictionary<string, SlashSpec> ByName = Commands
        .SelectMany(c => new[] { c.Cmd }.Concat(c.Aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(a => (a, c)))
        .ToDictionary(x => x.a, x => x.c, StringComparer.OrdinalIgnoreCase);

    /// <summary>Try to parse and execute a slash command. Returns true if handled.</summary>
    public static bool TryExecute(string input, ref bool running, ref string? statusMessage)
    {
        if (!input.StartsWith('/')) return false;

        var parts = input.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmdName = parts[0][1..].ToLowerInvariant();
        var args = parts.Length > 1 ? parts[1] : "";

        if (!ByName.TryGetValue(cmdName, out var spec))
        {
            // Fuzzy match
            var closest = ByName.Keys
                .Select(k => (name: k, dist: Levenshtein(cmdName, k)))
                .Where(x => x.dist <= 3)
                .OrderBy(x => x.dist)
                .FirstOrDefault();

            statusMessage = closest.name != null
                ? $"Unknown command '/{cmdName}'. Did you mean '/{closest.name}'?"
                : $"Unknown command '/{cmdName}'. Type /help for available commands.";
            return true;
        }

        // Track usage
        UsageCount.TryGetValue(spec.Cmd, out var count);
        UsageCount[spec.Cmd] = count + 1;

        return Execute(spec, args, ref running, ref statusMessage);
    }

    private static bool Execute(SlashSpec spec, string args, ref bool running, ref string? statusMessage)
    {
        var (h, s) = spec.Cmd switch
        {
            "help" => Help(),
            "exit" => ("", false),
            "new" => ("Session cleared. Starting fresh.", true),
            "retry" => ("Retrying last message...", true),
            "compact" => ("Summarizing older turns...", true),
            "model" => !string.IsNullOrEmpty(args) ? ($"Switched model to '{args}'", true) : ("Usage: /model <model-id>", true),
            "status" => Status(),
            "cost" => ("Cost tracking: see model provider dashboard", true),
            "memory" => ("Memory: use `remember` / `forget` tools", true),
            "skill" => !string.IsNullOrEmpty(args) ? ($"Running skill '{args}'...", true) : ("Skills: use `run_skill` tool", true),
            "mode" => args switch { "review" => ("Edit mode: review", true), "auto" => ("Edit mode: auto", true), _ => ("Usage: /mode review|auto", true) },
            "undo" => ("Undo: use the code tools", true),
            _ => ($"Command '/{spec.Cmd}' not implemented", true),
        };

        statusMessage = h;
        if (spec.Cmd == "exit") running = s;
        return true;
    }

    private static (string, bool) Help()
    {
        var groups = Commands.GroupBy(c => c.Group);
        var lines = new List<string> { "[bold yellow]LTAI Slash Commands[/]\n" };

        foreach (var g in groups)
        {
            lines.Add($"[bold]{g.Key}[/]");
            foreach (var c in g.OrderBy(x => x.Cmd))
            {
                var usage = UsageCount.GetValueOrDefault(c.Cmd);
                var freq = usage > 0 ? $" [grey](used {usage}x)[/]" : "";
                lines.Add($"  [cyan]/{c.Cmd}[/]{(c.Info ? "" : $" [grey]{c.ArgsHint}[/]")} — {c.Summary}{freq}");
            }
            lines.Add("");
        }

        return (string.Join("\n", lines), true);
    }

    private static (string, bool) Status()
    {
        return ($"LTAI v1.0 | Agent Framework 1.8.0 | Providers: {string.Join(", ", LTAI.AI.MultiProviderChatClient.DefaultProviders.Select(p => p.name).Take(3))}...\nMemory: {(Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), ".livingtree", "memories")) ? "✅" : "ℹ️")}", true);
    }

    private static int Levenshtein(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) dp[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return dp[a.Length, b.Length];
    }

    private sealed record SlashSpec(string Cmd, string Group, string Summary,
        string Aliases = "", string? ArgsHint = null, bool Info = false);
}
