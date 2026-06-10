using System.Text;
using LTAI.Agent.Tools;
using LTAI.Core.Commands;
using LTAI.Core.I18n;
using LTAI.Core.Configuration;
using LTAI.TUI.Services;
using Spectre.Console;

namespace LTAI.TUI;

/// <summary>
/// Slash command system — thin static facade.
/// Heavy handler logic is in <see cref="CommandRouter"/> (injectable, testable).
/// Kept here: command definitions, cascade menu, suggestions, picker, thin dispatch.
/// </summary>
public static class SlashCommands
{
    /// <summary>Injected command router — handles all heavy command execution.</summary>
    private static CommandRouter? _router;
    public static CommandRouter? Router { get => _router; set => _router = value; }

    public static string? PendingSnippetFill { get; set; }
    public static string? PendingInput { get; set; }
    public static string? PendingBuildResult { get; set; }
    public static string? PendingTextPrompt { get; set; }
    public static bool PendingTextIsSecret { get; set; }
    public static string? PendingTextPrefix { get; set; }

    // ═══ Cascade menu ═══
    public static string[] CascadeStack = [];
    public static string[] CascadeItems = [];
    public static int CascadeSel;
    public static string CascadeCmd = "";
    public static bool InCascadeMenu => CascadeItems.Length > 0;

    public static void OpenCascadeMenu(string cmd, string? args = null)
    {
        CascadeCmd = cmd;
        CascadeStack = args != null ? new[] { args } : [];
        FillCascade();
    }

    public static bool HandleCascadeKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.UpArrow && CascadeSel > 0) { CascadeSel--; return true; }
        if (key.Key == ConsoleKey.DownArrow && CascadeSel < CascadeItems.Length - 1) { CascadeSel++; return true; }
        if (key.Key == ConsoleKey.Enter) return CascadeEnter();
        if (key.Key == ConsoleKey.Escape) return CascadeEsc();
        return true;
    }

    static bool CascadeEnter()
    {
        var idx = CascadeSel;
        if (CascadeStack.Length > 0 && idx == 0) { CascadeStack = CascadeStack[..^1]; FillCascade(); return true; }
        if (idx < 0 || idx >= CascadeItems.Length) return true;
        var picked = CascadeItems[idx].Split(' ')[0].TrimEnd('…');
        var next = CascadeStack.Length > 0 ? CascadeStack.Append(picked).ToArray() : new[] { picked };
        if (CascadeRoutes.Resolve(CascadeCmd, next) != null)
        {
            CascadeStack = CascadeStack.Append(picked).ToArray();
            FillCascade();
            return true;
        }

        // Leaf node: check if free-text input is needed
        var promptMsg = CascadeRoutes.GetLeafPrompt(CascadeCmd, next);
        if (promptMsg != null)
        {
            // Instead of AnsiConsole.Prompt/Ask (conflicts with LiveDisplayContext),
            // activate an inline overlay for text input.
            PendingTextPrompt = promptMsg;
            PendingTextIsSecret = promptMsg.Contains("API Key");
            PendingTextPrefix = $"/{CascadeCmd} " + string.Join(" ", next);
            CloseCascadeMenu();
            return false;
        }

        var cmd2 = $"/{CascadeCmd} " + string.Join(" ", next);
        PendingInput = cmd2;
        CloseCascadeMenu();
        return false;
    }

    static bool CascadeEsc()
    {
        if (CascadeStack.Length > 0) { CascadeStack = CascadeStack[..^1]; FillCascade(); return true; }
        CloseCascadeMenu();
        return false;
    }

    public static void CloseCascadeMenu()
    {
        CascadeStack = [];
        CascadeItems = [];
        CascadeSel = 0;
        CascadeCmd = "";
    }

    public static string BuildCascadeText()
    {
        var path = CascadeStack.Length > 0 ? $"/{CascadeCmd} {string.Join(" ", CascadeStack)}" : $"/{CascadeCmd}";
        var sb = new StringBuilder();
        sb.AppendLine($"[bold yellow]{path}[/]");
        for (int i = 0; i < CascadeItems.Length; i++)
        {
            var parts = CascadeItems[i].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var s = parts[0];
            var desc = parts.Length > 1 ? "  " + parts[1] : "";
            var arrow = i == CascadeSel ? "[yellow]▸[/]" : " ";
            sb.AppendLine($"  {arrow} [cyan]{s,-12}[/]{desc}");
        }
        sb.Append("[dim]↑↓=选择  Enter=确认  Esc=返回  Ctrl+Q=退出[/]");
        return sb.ToString();
    }

    static void FillCascade()
    {
        var node = CascadeRoutes.Resolve(CascadeCmd, CascadeStack);
        var choices = node?.Items ?? [];
        if (CascadeStack.Length > 0)
        {
            CascadeItems = new[] { "← 返回" }.Concat(choices).ToArray();
            CascadeSel = choices.Length > 0 ? 1 : 0;
        }
        else
        {
            CascadeItems = choices;
            CascadeSel = 0;
        }
    }

    public static ICommandParser Parser { get; set; } = new CommandParser();

    internal static readonly Dictionary<string, int> UsageCount = new();

    internal sealed record SlashSpec(string Cmd, string Group, string Summary,
        string? ArgsHint = null);

    internal static readonly SlashSpec[] Commands =
    {
        new("help",    "聊天",  "显示帮助信息"),
        new("new",     "聊天",  "新建会话（清空历史）"),
        new("retry",   "聊天",  "重发上一条消息"),
        new("compact", "聊天",  "压缩汇总历史消息"),
        new("model",   "模型",  "模型管理: l0/l1/l2 级联选择", "l0|l1|l2"),
        new("models",  "信息",  "当前模型配置 + 在线模型列表"),
        new("status",  "信息",  "显示当前配置和统计"),
        new("jobs",    "信息",  "后台作业: list|watch|cancel|show"),
        new("cost",    "信息",  "显示本轮预估 Token 费用"),
        new("config",  "设置",  "设置: apikey|export|import"),
        new("snippet", "扩展",  "常用语管理: list|save|use|delete|rename|edit"),
        new("workflow","扩展",  "编排工作流: list|reload|show|open"),
        new("pipe",    "扩展",  "管道: list|run|stop"),
        new("mode",    "编辑",  "编辑模式: review|auto", "review|auto"),
        new("undo",    "编辑",  "撤销上次编辑"),
        new("ls",      "文件",  "列出当前目录内容"),
        new("cd",      "文件",  "切换工作目录", "目录路径"),
        new("pwd",     "文件",  "显示当前目录"),
        new("todos",   "信息",  "查看待办事项列表"),
        new("approve", "计划",  "批准当前计划并开始执行"),
        new("plan",    "计划",  "查看当前计划状态"),
        new("lang",    "设置",  "切换语言: zh-CN|en-US"),
        new("git",     "文件",  "Git: status|diff|log|add|commit|pull|push"),
        new("graph",   "扩展",  "代码/文档索引: init|search <query>"),
        new("agents",  "信息",  "Agent 列表: list|show <name>"),
        new("tools",   "信息",  "工具列表: list|domain <name>"),
        new("mcp",     "扩展",  "MCP 管理: list|status|tools"),
        new("spec",    "开发",  "Spec 管理: list|new|show|edit|delete|status|plan|tasks"),
        new("prompt",  "扩展",  "Agent Prompt 编辑器: list|show|edit"),
        new("keys",    "信息",  "显示键盘快捷键一览"),
        new("exit",    "高级",  "退出应用"),
    };

    // Aliases sourced from CommandParser.KnownCommands to eliminate duplication
    private static readonly Dictionary<string, SlashSpec> ByName = Commands
        .SelectMany(c =>
        {
            var parserEntry = CommandParser.KnownCommands
                .FirstOrDefault(p => string.Equals(p.cmd, c.Cmd, StringComparison.OrdinalIgnoreCase));
            var aliases = parserEntry.aliases?.Where(a => a.Length > 0) ?? [];
            return new[] { (alias: c.Cmd, spec: c) }.Concat(
                aliases.Select(a => (alias: a, spec: c)));
        })
        .DistinctBy(x => x.alias, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(x => x.alias, x => x.spec, StringComparer.OrdinalIgnoreCase);

    public static string[] GetSuggestions(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return [];
        return ByName.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k)
            .ToArray();
    }

    public sealed record SuggestionItem(
        string DisplayText, string Completion, string Group, bool IsAlias);

    private static string[] GetAliases(string cmd)
    {
        var entry = CommandParser.KnownCommands
            .FirstOrDefault(p => string.Equals(p.cmd, cmd, StringComparison.OrdinalIgnoreCase));
        return entry.aliases?.Where(a => a.Length > 0).ToArray() ?? [];
    }

    public static List<SuggestionItem> GetSuggestionItems(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return [];

        if (prefix == "/")
        {
            var allItems = new List<SuggestionItem>();
            foreach (var spec in Commands)
            {
                var aliases = GetAliases(spec.Cmd);
                var aliasText = aliases.Length > 0
                    ? $", {string.Join(", ", aliases)}"
                    : "";
                allItems.Add(new SuggestionItem(
                    $"/{spec.Cmd}{aliasText}  [dim]{spec.Group}[/]", $"/{spec.Cmd}", spec.Group, false));
            }
            return allItems;
        }

        var sp = prefix.StartsWith("/") ? prefix[1..] : prefix;
        var matchedCmds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in ByName.Keys)
            if (k.StartsWith(sp, StringComparison.OrdinalIgnoreCase))
                matchedCmds.Add(ByName[k].Cmd);

        var items = new List<SuggestionItem>();
        foreach (var spec in Commands)
        {
            if (!matchedCmds.Contains(spec.Cmd)) continue;
            var matchingAliases = GetAliases(spec.Cmd)
                .Where(a => a.StartsWith(sp, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var aliasText = matchingAliases.Length > 0
                ? $", {string.Join(", ", matchingAliases)}"
                : "";
            items.Add(new SuggestionItem(
                $"/{spec.Cmd}{aliasText}  [dim]{spec.Group}[/]", $"/{spec.Cmd}", spec.Group, false));
        }

        return items.OrderBy(i => i.Group).ThenBy(i => i.Completion).ToList();
    }

    public static string? ShowPicker()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[bold yellow]📋 LTAI 命令列表[/]\n");
        var flatList = new List<string>();
        var idx = 1;

        foreach (var g in Commands.GroupBy(c => c.Group).OrderBy(x => x.Key))
        {
            sb.AppendLine($"[bold]{g.Key}[/]");
            foreach (var c in g.OrderBy(x => x.Cmd))
            {
                var hint = string.IsNullOrEmpty(c.ArgsHint) ? "" : $" [grey]{c.ArgsHint}[/]";
                sb.AppendLine($"  [cyan]{idx,2}.[/] [cyan]/{c.Cmd}[/]{hint}  {c.Summary}");
                flatList.Add(c.Cmd);
                idx++;
            }
            sb.AppendLine();
        }
        sb.AppendLine("[grey]输入编号选择, 回车取消[/]");

        AnsiConsole.WriteLine();
        AnsiConsole.Markup(sb.ToString());
        var input = AnsiConsole.Ask<string>("[grey]编号:[/]");
        if (string.IsNullOrWhiteSpace(input)) return null;

        if (int.TryParse(input, out var num) && num >= 1 && num <= flatList.Count)
        {
            var cmdName = flatList[num - 1];
            var spec = Commands.FirstOrDefault(c => c.Cmd == cmdName);
            if (spec == null) return null;
            if (!string.IsNullOrEmpty(spec.ArgsHint))
            {
                AnsiConsole.Markup($"[grey]/{spec.Cmd} {spec.ArgsHint}:[/] ");
                var args = Console.ReadLine() ?? "";
                return $"/{spec.Cmd} {args}";
            }
            return $"/{spec.Cmd}";
        }
        return null;
    }

    /// <summary>Try to parse and execute a slash command. Returns (handled, statusMessage).</summary>
    public static async Task<(bool Handled, string? StatusMessage)> TryExecuteAsync(string input)
    {
        var parsed = Parser.Parse(input);
        if (parsed is ChatMessageCommand or EmptyCommand)
            return (false, null);

        var cmdName = parsed switch
        {
            UnknownCommand u => u.CmdName,
            _ => parsed.GetType().Name.Replace("Command", "").ToLowerInvariant(),
        };
        UsageCount.TryGetValue(cmdName, out var count);
        UsageCount[cmdName] = count + 1;

        return await ExecuteParsedAsync(parsed).ConfigureAwait(false);
    }

    private static async Task<(bool Handled, string? StatusMessage)> ExecuteParsedAsync(Command cmd)
    {
        var router = _router;

        switch (cmd)
        {
            case ExitCommand:
                return (true, null);

            case NewSessionCommand:
                return (true, "Session cleared. Starting fresh.");

            case RetryCommand:
                return (true, "Retrying last message...");

            case CompactCommand:
                return (true, "Summarizing older turns...");

            case CostCommand:
                var cost = UsageTracker.EstimatedCost;
                var model = UsageTracker.ActiveModel;
                var prompt = UsageTracker.PromptTokens;
                var completion = UsageTracker.CompletionTokens;
                var total = UsageTracker.TotalTokens;
                var requests = UsageTracker.Requests;
                var rate = UsageTracker.CacheHitRate;
                var saved = UsageTracker.CacheSavedDisplay;
                return (true, $"[bold yellow]📊 使用统计[/]\n" +
                    $"  [cyan]模型:[/] {model}\n" +
                    $"  [cyan]请求:[/] {requests:N0}\n" +
                    $"  [cyan]Token:[/] {prompt:N0} + {completion:N0} = [bold]{total:N0}[/]\n" +
                    $"  [cyan]费用:[/] [bold]¥{cost:F4}[/]\n" +
                    $"  [cyan]缓存命中:[/] {rate:F1}% ({saved})\n" +
                    $"  [cyan]运行时间:[/] {UsageTracker.Uptime:hh\\:mm\\:ss}");

            case UndoCommand:
                var undoMsg = ChatLayout.TryUndoCallback != null && ChatLayout.TryUndoCallback()
                    ? "[green]已撤销上一步操作[/]"
                    : "[yellow]没有可撤销的操作[/]";
                return (true, undoMsg);

            case ApproveCommand:
                return (true, PlanTools.ApprovePlan() + "\n" + PlanTools.StartExecution());

            case PlanCommand:
                return (true, PlanTools.PlanStatus());

            case TodosCommand:
                var todoSummary = LTAI.Agent.Tooling.AgentModeObserver.TodoSummary;
                var rem = LTAI.Agent.Tooling.AgentModeObserver.RemainingTodos;
                var tot = LTAI.Agent.Tooling.AgentModeObserver.TotalTodos;
                if (tot == 0)
                    return (true, "[yellow]📋 暂无待办事项[/]");
                var todoText = !string.IsNullOrEmpty(todoSummary)
                    ? todoSummary
                    : $"{rem} 项未完成 / {tot} 项总计";
                var modeName = LTAI.Agent.Tooling.AgentModeObserver.CurrentMode;
                return (true, $"[bold]📋 待办清单[/]  |  模式: {LTAI.Agent.Tooling.AgentModeObserver.ModeIcon} {modeName}\n\n{todoText}");

            case ModeCommand mc:
                var mode = mc.Args.ToLowerInvariant() switch { "review" => "review", "auto" => "auto", _ => "" };
                if (mode == "") return (true, "Usage: /mode review|auto");
                ChatLayout.EditMode = mode;
                return (true, $"Edit mode: {mode} (style: {(mode == "review" ? "批注修改" : "直接编辑")})");

            case LangCommand lc:
                var lang = lc.Args.Trim().ToLowerInvariant();
                if (lang is "zh-cn" or "zh" or "cn") { Locale.SetLang("zh-CN"); ThemeService.Language = "zh-CN"; ThemeService.Save(); return (true, "已切换界面语言: 中文"); }
                else if (lang is "en-us" or "en" or "us") { Locale.SetLang("en-US"); ThemeService.Language = "en-US"; ThemeService.Save(); return (true, "Language switched: English"); }
                else return (true, $"Usage: /lang zh-CN|en-US (current: {Locale.CurrentLang})");

            case SkillCommand sc:
                if (string.IsNullOrEmpty(sc.Args))
                    return (true, "[yellow]用法: /skill <技能名> — 列出可用技能: /skills[/]");
                else
                    return (true, $"[yellow]⏳ 运行技能 '{sc.Args}'...[/]\n[grey]技能在后台运行中，结果将出现在对话中[/]");

            case UnknownCommand uc:
                var msg = uc.Suggestion != null
                    ? $"Unknown command '/{uc.CmdName}'. Did you mean '/{uc.Suggestion}'?"
                    : $"Unknown command '/{uc.CmdName}'. Type /help for available commands.";
                return (true, msg);

            // ── Model command: cascade if empty args ──
            case ModelCommand mc when string.IsNullOrWhiteSpace(mc.Args) && CascadeRoutes.Resolve("model", []) != null:
                OpenCascadeMenu("model");
                return (true, BuildCascadeText());

            // ── Commands delegated to router ──
            default:
                if (router == null) return (false, null);
                var result = await router.ExecuteAsync(cmd).ConfigureAwait(false);
                switch (result)
                {
                    case SuccessResult(var markup, var snippetFill):
                        if (snippetFill != null) PendingSnippetFill = snippetFill;
                        return (true, markup);

                    case CascadeResult(var rc, var ra):
                        OpenCascadeMenu(rc, ra);
                        return (true, BuildCascadeText());

                    case RedirectResult(var input):
                        PendingInput = input;
                        return (true, "");

                    default:
                        return (false, null);
                }
        }
    }

}
