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
        new("help",    "聊天",  "显示帮助信息", "?,帮助,帮助"),
        new("new",     "聊天",  "新建会话（清空历史）", "reset,clear,新,新建"),
        new("retry",   "聊天",  "重发上一条消息", "重试,重发"),
        new("compact", "聊天",  "压缩汇总历史消息", "压缩,汇总"),
        new("model",   "设置",  "切换 AI 模型", "", "model-id"),
        new("status",  "信息",  "显示当前配置和统计", "状态,统计"),
        new("monitor", "信息",  "实时仪表盘 — Provider 状态/延迟/成本", "监控,仪表盘"),
        new("cost",    "信息",  "显示本轮预估费用", "费用,花费"),
        new("memory",  "扩展",  "管理记忆文件", "记忆"),
        new("skill",   "扩展",  "列出/运行技能", "", "技能名"),
        new("mode",    "代码",  "编辑模式: review|auto", "", "review|auto"),
        new("undo",    "代码",  "撤销上次编辑", "撤销"),
        new("cd",      "文件",  "切换工作目录", "", "目录路径"),
        new("pwd",     "文件",  "显示当前目录", "目录"),
        new("approve", "计划",  "批准当前计划并开始执行", "yes,confirm,批准,确认"),
        new("plan",    "计划",  "查看当前计划状态", "计划状态"),
        new("exit",    "高级",  "退出应用", "quit,q,退出,退出"),
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
            "monitor" => Monitor(),
            "cost" => ("Cost tracking: see model provider dashboard", true),
            "memory" => ("Memory: use `remember` / `forget` tools", true),
            "skill" => !string.IsNullOrEmpty(args) ? ($"Running skill '{args}'...", true) : ("Skills: use `run_skill` tool", true),
            "mode" => args switch { "review" => ("Edit mode: review", true), "auto" => ("Edit mode: auto", true), _ => ("Usage: /mode review|auto", true) },
            "cd" => ChangeDir(args),
            "pwd" => (Directory.GetCurrentDirectory(), true),
            "approve" => (LTAI.Agent.Tools.PlanTools.ApprovePlan() + "\n" + LTAI.Agent.Tools.PlanTools.StartExecution(), true),
            "plan" => (LTAI.Agent.Tools.PlanTools.PlanStatus(), true),
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
        var lines = new List<string> { "[bold yellow]LTAI 命令列表[/]\n" };

        foreach (var g in groups)
        {
            lines.Add($"[bold]{g.Key}[/]");
            foreach (var c in g.OrderBy(x => x.Cmd))
            {
                var usage = UsageCount.GetValueOrDefault(c.Cmd);
                var freq = usage > 0 ? $" [grey](已用 {usage} 次)[/]" : "";
                lines.Add($"  [cyan]/{c.Cmd}[/]{(c.Info ? "" : $" [grey]{c.ArgsHint}[/]")} — {c.Summary}{freq}");
            }
            lines.Add("");
        }

        return (string.Join("\n", lines), true);
    }

    private static (string, bool) Monitor()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[bold yellow]📊 LTAI 实时监控[/]\n");

        // Provider 状态
        sb.AppendLine("[bold]Provider 统计[/]");
        sb.AppendLine("| Provider | 状态 | Token | 缓存命中 |");
        sb.AppendLine("|----------|------|-------|----------|");
        sb.AppendLine($"| DeepSeek | [green]✅[/] | {LTAI.Core.Configuration.UsageTracker.PromptTokens:N0} | {LTAI.Core.Configuration.UsageTracker.CacheHitRate:F1}% |");
        sb.AppendLine();

        // 会话统计
        sb.AppendLine("[bold]会话统计[/]");
        sb.AppendLine($"Token: {LTAI.Core.Configuration.UsageTracker.TotalTokens:N0} | 请求: {LTAI.Core.Configuration.UsageTracker.Requests}");
        sb.AppendLine($"费用: {LTAI.Core.Configuration.UsageTracker.CostDisplay}");
        sb.AppendLine($"缓存命中: {LTAI.Core.Configuration.UsageTracker.CacheHitRate:F1}%");
        sb.AppendLine($"上下文: {LTAI.Core.Configuration.UsageTracker.ContextText()}");
        sb.AppendLine($"运行时间: {LTAI.Core.Configuration.UsageTracker.Uptime:hh\\:mm\\:ss}");
        sb.AppendLine();

        // 模型信息
        sb.AppendLine("[bold]模型[/]");
        sb.AppendLine($"当前: {LTAI.Core.Configuration.UsageTracker.ActiveModel}");
        sb.AppendLine($"余额: {LTAI.Core.Configuration.UsageTracker.BalanceDisplay}");

        return (sb.ToString(), true);
    }

    private static (string, bool) Status()
    {
        return ($"[bold]LTAI 状态[/]\n"
              + $"模型: {LTAI.Core.Configuration.UsageTracker.ActiveModel}\n"
              + $"提供商: {string.Join(", ", LTAI.AI.MultiProviderChatClient.DefaultProviders.Select(p => p.name).Take(3))}...\n"
              + $"目录: {Directory.GetCurrentDirectory()}\n"
              + $"Token: {LTAI.Core.Configuration.UsageTracker.TotalTokens:N0} | 请求: {LTAI.Core.Configuration.UsageTracker.Requests} | 费用: {LTAI.Core.Configuration.UsageTracker.CostDisplay}\n"
              + $"缓存: {LTAI.Core.Configuration.UsageTracker.CacheHitRate:F1}% | 上下文: {LTAI.Core.Configuration.UsageTracker.ContextText()}", true);
    }

    /// <summary>Change working directory, validated against sandbox root.</summary>
    private static (string, bool) ChangeDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ($"当前目录: {Directory.GetCurrentDirectory()}", true);
        try
        {
            var newDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
            if (!Directory.Exists(newDir)) return ($"目录不存在: {newDir}", true);
            // 沙箱检查：不能逃逸工作区根目录
            var root = Path.GetFullPath(_rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!newDir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return ($"拒绝: 路径 '{path}' 逃逸了工作区 '{_rootPath}'", true);
            Directory.SetCurrentDirectory(newDir);
            return ($"已切换到: {newDir}", true);
        }
        catch (Exception ex) { return ($"切换失败: {ex.Message}", true); }
    }

    /// <summary>Set the sandbox root (called by TuiApp on startup).</summary>
    public static void SetRootPath(string root) => _rootPath = root;
    private static string _rootPath = Directory.GetCurrentDirectory();

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
