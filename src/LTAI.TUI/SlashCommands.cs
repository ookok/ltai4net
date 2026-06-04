using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LTAI.AI;
using LTAI.Agent;
using LTAI.Agent.Snippets;
using LTAI.Agent.Tools;
using LTAI.Core.I18n;
using LTAI.Agent.Workflows;
using LTAI.Core.Configuration;
using LTAI.Core.Commands;
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

    public static LocalEmbedder? Embedder { get; set; }
    public static MultiProviderChatClient? RouterClient { get; set; }
    public static IHttpClientFactory? HttpFactory { get; set; }
    public static SnippetStore? SnippetStore { get; set; }
    public static string? PendingSnippetFill { get; set; }
    public static YAMLWorkflowRegistry? WorkflowRegistry { get; set; }
    public static AgentWorkflows? Pipes { get; set; }
    public static ModelMetadataProvider? ModelsProvider { get; set; }
    public static BackgroundJobService? Jobs { get; set; }
    public static string? L1Model { get; set; }
    public static string? L2Model { get; set; }
    public static string? ActiveProvider { get; set; }
    public static string? PendingInput { get; set; }
    public static string? PendingBuildResult { get; set; }
    public static LTAI.Agent.Vector.CgGraph? CgGraph { get; set; }
    public static LTAI.Agent.Vector.KbGraph? KbGraph { get; set; }

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
        var ri = CascadeStack.Length > 0 ? idx - 1 : idx;
        if (ri < 0 || ri >= CascadeItems.Length) return true;
        var picked = CascadeItems[ri].Split(' ')[0].TrimEnd('…');
        var next = CascadeStack.Length > 0 ? CascadeStack.Append(picked).ToArray() : new[] { picked };
        if (CascadeRoutes.Resolve(CascadeCmd, next) != null)
        {
            CascadeStack = CascadeStack.Append(picked).ToArray();
            FillCascade();
            return true;
        }
        var cmd = $"/{CascadeCmd} " + string.Join(" ", CascadeStack.Append(picked));
        PendingInput = cmd;
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
        CascadeItems = CascadeStack.Length > 0 ? new[] { "← 返回" }.Concat(choices).ToArray() : choices;
        CascadeSel = CascadeStack.Length > 0 ? 1 : 0;
    }

    private static readonly HttpClient _sharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static ICommandParser Parser { get; set; } = new CommandParser();

    public static readonly Dictionary<string, ProviderInfo> KnownProviders = BuildKnownProviders();

    public sealed record ProviderInfo(string EnvVar, string Endpoint, string Model);

    private static Dictionary<string, ProviderInfo> BuildKnownProviders()
    {
        var d = KnownKeys.All
            .Where(k => k.Endpoint != null && k.Model != null)
            .ToDictionary(k => k.Service, k => new ProviderInfo(k.EnvVar, k.Endpoint!, k.Model!));
        d["Ollama"]   = new("", "http://localhost:11434/v1", "llama3.2");
        d["LMStudio"] = new("", "http://localhost:1234/v1",  "local-model");
        d["vLLM"]     = new("", "http://localhost:8000/v1",  "meta-llama/Llama-3.2-3B-Instruct");
        return d;
    }

    private static readonly Dictionary<string, int> UsageCount = new();

    private sealed record SlashSpec(string Cmd, string Group, string Summary,
        string Aliases = "", string? ArgsHint = null, bool Info = false);

    private static readonly SlashSpec[] Commands =
    {
        new("help",    "聊天",  "显示帮助信息", "?,帮助"),
        new("new",     "聊天",  "新建会话（清空历史）", "reset,clear,新,新建"),
        new("retry",   "聊天",  "重发上一条消息", "重试,重发"),
        new("compact", "聊天",  "压缩汇总历史消息", "压缩,汇总"),
        new("model",   "模型",  "模型管理: l0/l1/l2 级联选择", "", "l0|l1|l2"),
        new("models",  "信息",  "当前模型配置 + 在线模型列表", "在线模型,provider列表"),
        new("status",  "信息",  "显示当前配置和统计", "状态,统计"),
        new("jobs",    "信息",  "后台作业: list|watch|cancel|show", "job,任务"),
        new("cost",    "信息",  "显示本轮预估 Token 费用", "费用,花费"),
        new("config",  "设置",  "设置: apikey|export|import", "apikey,导出,导入"),
        new("snippet", "扩展",  "常用语管理: list|save|use|delete|rename|edit", "snip,常用语,常用,短语"),
        new("workflow","扩展",  "编排工作流: list|reload|show|open", "wf,编排,工作流"),
        new("pipe",    "扩展",  "管道: list|run|stop", "pipeline,顺序,并发"),
        new("mode",    "编辑",  "编辑模式: review|auto", "", "review|auto"),
        new("undo",    "编辑",  "撤销上次编辑", "撤销"),
        new("ls",      "文件",  "列出当前目录内容", "dir,列表"),
        new("cd",      "文件",  "切换工作目录", "", "目录路径"),
        new("pwd",     "文件",  "显示当前目录", "目录"),
        new("approve", "计划",  "批准当前计划并开始执行", "yes,confirm,批准,确认"),
        new("plan",    "计划",  "查看当前计划状态", "计划状态"),
        new("lang",    "设置",  "切换语言: zh-CN|en-US", "语言,language"),
        new("git",     "文件",  "Git: status|diff|log|add|commit|pull|push", "g"),
        new("build",   "扩展",  "全量重建代码索引", "b,构建"),
        new("exit",    "高级",  "退出应用", "quit,q,退出"),
    };

    private static readonly Dictionary<string, SlashSpec> ByName = Commands
        .SelectMany(c => new[] { c.Cmd }.Concat(c.Aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(a => (a, c)))
        .ToDictionary(x => x.a, x => x.c, StringComparer.OrdinalIgnoreCase);

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

    public static List<SuggestionItem> GetSuggestionItems(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return [];

        if (prefix == "/")
        {
            var allItems = new List<SuggestionItem>();
            foreach (var spec in Commands)
            {
                allItems.Add(new SuggestionItem(
                    $"/{spec.Cmd}  [dim]{spec.Group}[/]", $"/{spec.Cmd}", spec.Group, false));
                foreach (var alias in spec.Aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    allItems.Add(new SuggestionItem(
                        $"{alias}  [dim]{spec.Group} → /{spec.Cmd}[/]", $"/{spec.Cmd}", spec.Group, true));
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
            items.Add(new SuggestionItem(
                $"/{spec.Cmd}  [dim]{spec.Group}[/]", $"/{spec.Cmd}", spec.Group, false));
            foreach (var alias in spec.Aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(a => a.Length > 0 && a.StartsWith(sp, StringComparison.OrdinalIgnoreCase)))
                items.Add(new SuggestionItem(
                    $"{alias}  [dim]{spec.Group} → /{spec.Cmd}[/]", $"/{spec.Cmd}", spec.Group, true));
        }

        return items.OrderBy(i => i.Group).ThenBy(i => i.IsAlias ? 1 : 0).ThenBy(i => i.Completion).ToList();
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
            var spec = Commands.FirstOrDefault(c => c.Cmd == cmdName)!;
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

    /// <summary>Try to parse and execute a slash command. Returns true if handled.</summary>
    public static bool TryExecute(string input, ref bool running, ref string? statusMessage)
    {
        var parsed = Parser.Parse(input);
        if (parsed is ChatMessageCommand or EmptyCommand)
            return false;

        var cmdName = parsed switch
        {
            UnknownCommand u => u.CmdName,
            _ => parsed.GetType().Name.Replace("Command", "").ToLowerInvariant(),
        };
        UsageCount.TryGetValue(cmdName, out var count);
        UsageCount[cmdName] = count + 1;

        return ExecuteParsed(parsed, ref running, ref statusMessage);
    }

    private static bool ExecuteParsed(Command cmd, ref bool running, ref string? statusMessage)
    {
        var router = _router;

        switch (cmd)
        {
            case ExitCommand:
                running = false;
                statusMessage = "";
                return true;

            case NewSessionCommand:
                statusMessage = "Session cleared. Starting fresh.";
                return true;

            case RetryCommand:
                statusMessage = "Retrying last message...";
                return true;

            case CompactCommand:
                statusMessage = "Summarizing older turns...";
                return true;

            case CostCommand:
                statusMessage = "Cost tracking: see model provider dashboard";
                return true;

            case PwdCommand:
                statusMessage = Directory.GetCurrentDirectory();
                return true;

            case UndoCommand:
                statusMessage = "Undo: use the code tools";
                return true;

            case ApproveCommand:
                statusMessage = PlanTools.ApprovePlan() + "\n" + PlanTools.StartExecution();
                return true;

            case PlanCommand:
                statusMessage = PlanTools.PlanStatus();
                return true;

            case ModeCommand mc:
                statusMessage = mc.Args switch { "review" => "Edit mode: review", "auto" => "Edit mode: auto", _ => "Usage: /mode review|auto" };
                return true;

            case LangCommand lc:
                var lang = lc.Args.Trim().ToLowerInvariant();
                if (lang is "zh-cn" or "zh" or "cn") { Locale.SetLang("zh-CN"); statusMessage = "已切换界面语言: 中文"; }
                else if (lang is "en-us" or "en" or "us") { Locale.SetLang("en-US"); statusMessage = "Language switched: English"; }
                else statusMessage = $"Usage: /lang zh-CN|en-US (current: {Locale.CurrentLang})";
                return true;

            case LsCommand lc:
                (statusMessage, _) = ListDir(lc.Args);
                return true;

            case CdCommand cd:
                (statusMessage, _) = ChangeDir(cd.Args);
                return true;

            case SkillCommand sc:
                statusMessage = !string.IsNullOrEmpty(sc.Args) ? $"Running skill '{sc.Args}'..." : "Skills: use `run_skill` tool";
                return true;

            case HelpCommand:
                statusMessage = Help();
                return true;

            case StatusCommand:
                statusMessage = Status();
                return true;

            case GitCommand gc:
                statusMessage = RunGit(gc.Args);
                return true;

            case GraphCommand { Args: "" or null }:
            case GraphCommand { Args: "init" }:
                if (CgGraph == null) { statusMessage = "CodeGraph not available"; return true; }
                statusMessage = "Building code graph + document index...";
                _ = Task.Run(async () =>
                {
                    var codeResult = await CgGraph.BuildAsync().ConfigureAwait(false);
                    var docResult = "";
                    if (KbGraph != null)
                        docResult = await KbGraph.BuildDocumentIndexAsync(Directory.GetCurrentDirectory()).ConfigureAwait(false);
                    PendingBuildResult = $"Code: {codeResult.Replace("\n", " | ")}\nDocs: {docResult}";
                });
                return true;
            case GraphCommand { Args: not null } g when g.Args.StartsWith("search"):
                if (CgGraph == null) { statusMessage = "CodeGraph not available"; return true; }
                var query = g.Args.Length > 7 ? g.Args[7..].Trim() : "";
                if (string.IsNullOrWhiteSpace(query)) { statusMessage = "Usage: /graph search <query>"; return true; }
                statusMessage = "Searching graph...";
                _ = Task.Run(async () =>
                {
                    var sb = new System.Text.StringBuilder();
                    var codeResult = await CgGraph.QueryAsync(query, topK: 3).ConfigureAwait(false);
                    if (!codeResult.StartsWith("No relevant") && !codeResult.StartsWith("Code graph not built"))
                        sb.AppendLine(codeResult);
                    if (KbGraph != null)
                    {
                        try
                        {
                            var kbResults = await KbGraph.QueryAsync(query, topK: 5).ConfigureAwait(false);
                            if (kbResults.Count > 0)
                                sb.AppendLine("## Relevant Knowledge:\n" + string.Join("\n", kbResults.Select(r => "- " + r)));
                        }
                        catch { }
                    }
                    PendingBuildResult = sb.Length > 0 ? sb.ToString().Replace("\n", " | ") : "No results found.";
                });
                return true;

            case UnknownCommand uc:
                statusMessage = uc.Suggestion != null
                    ? $"Unknown command '/{uc.CmdName}'. Did you mean '/{uc.Suggestion}'?"
                    : $"Unknown command '/{uc.CmdName}'. Type /help for available commands.";
                return true;

            // ── Model command: cascade if empty args ──
            case ModelCommand mc when string.IsNullOrWhiteSpace(mc.Args) && CascadeRoutes.Resolve("model", []) != null:
                OpenCascadeMenu("model");
                statusMessage = BuildCascadeText();
                return true;

            // ── Commands delegated to router ──
            default:
                if (router == null) return false;
                var result = router.Execute(cmd);
                switch (result)
                {
                    case SuccessResult(var markup, var snippetFill):
                        if (snippetFill != null) PendingSnippetFill = snippetFill;
                        statusMessage = markup;
                        return true;

                    case CascadeResult(var rc, var ra):
                        OpenCascadeMenu(rc, ra);
                        statusMessage = BuildCascadeText();
                        return true;

                    case RedirectResult(var input):
                        PendingInput = input;
                        statusMessage = "";
                        return true;

                    default:
                        return false;
                }
        }
    }

    // ═══════════════════════════════════════════
    //  Simple handlers kept in SlashCommands
    // ═══════════════════════════════════════════

    static string RunGit(string args)
    {
        if (string.IsNullOrWhiteSpace(args) || args == "help")
        {
            return "[bold]Git 命令[/]\n"
                + "  /git status    — 查看工作区状态（含结构化文件变更列表）\n"
                + "  /git diff      — 查看未暂存的变更差异\n"
                + "  /git diff --cached — 查看已暂存的变更\n"
                + "  /git log       — 查看提交历史\n"
                + "  /git add <file> — 暂存文件\n"
                + "  /git commit -m \"msg\" — 提交\n"
                + "  /git pull      — 拉取\n"
                + "  /git push      — 推送\n"
                + "  /git <任意 git 参数> — 直接透传";
        }

        try
        {
            var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
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
            var panel = new Panel(sb.ToString().TrimEnd())
                .Header(header)
                .Border(BoxBorder.Rounded)
                .Expand();
            AnsiConsole.Write(panel);
            return "";
        }
        catch (Exception ex)
        {
            return $"[red]git 错误: {ex.Message}[/]";
        }
    }

    static string Help()
    {
        var groups = Commands.GroupBy(c => c.Group);
        var lines = new List<string>
        {
            "[bold yellow]┌─────────────────────────────────────┐[/]",
            "[bold yellow]│         LTAI 命令列表                │[/]",
            "[bold yellow]└─────────────────────────────────────┘[/]",
            ""
        };
        foreach (var g in groups.OrderBy(x => x.Key))
        {
            lines.Add($"[bold]{g.Key}[/]");
            lines.Add("[grey]──[/]");
            foreach (var c in g.OrderBy(x => x.Cmd))
            {
                var freq = UsageCount.GetValueOrDefault(c.Cmd) > 0
                    ? $" [grey]({UsageCount[c.Cmd]}x)[/]" : "";
                var hint = string.IsNullOrEmpty(c.ArgsHint) ? "" : $" [dim]{c.ArgsHint}[/]";
                var summary = c.Summary;
                lines.Add($"  [cyan]/{c.Cmd,-10}[/]{hint,-18} {summary}{freq}");
            }
            lines.Add("");
        }
        lines.Add("[dim]提示: 输入 [yellow]/[/] 打开交互式命令选择器   |   ↑↓ 历史导航[/]");
        return string.Join("\n", lines);
    }

    static string Status()
    {
        return $"[bold]LTAI 状态[/]\n"
            + $"模型: {UsageTracker.ActiveModel}\n"
            + $"提供商: {string.Join(", ", MultiProviderChatClient.DefaultProviders.Select(p => p.name).Take(3))}...\n"
            + $"目录: {Directory.GetCurrentDirectory()}\n"
            + $"Token: {UsageTracker.TotalTokens:N0} | 请求: {UsageTracker.Requests} | 费用: {UsageTracker.CostDisplay}\n"
            + $"缓存: {UsageTracker.CacheHitRate:F1}% | 上下文: {UsageTracker.ContextText()}";
    }

    private static (string, bool) ChangeDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ($"当前目录: {Directory.GetCurrentDirectory()}", true);
        try
        {
            var newDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
            if (!Directory.Exists(newDir)) return ($"目录不存在: {newDir}", true);
            Directory.SetCurrentDirectory(newDir);
            return ($"已切换到: {newDir}", true);
        }
        catch (Exception ex) { return ($"切换失败: {ex.Message}", true); }
    }

    private static (string, bool) ListDir(string path)
    {
        try
        {
            var dir = !string.IsNullOrWhiteSpace(path)
                ? Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path))
                : Directory.GetCurrentDirectory();

            if (!Directory.Exists(dir)) return ($"[red]目录不存在:[/] {dir}", true);

            var root = new Tree($"[bold yellow]📂 {dir}[/]");
            var dirs = Directory.GetDirectories(dir)
                .Select(d => (name: Path.GetFileName(d), info: new DirectoryInfo(d)))
                .OrderBy(x => x.name);
            var files = Directory.GetFiles(dir)
                .Select(f => (name: Path.GetFileName(f), info: new FileInfo(f)))
                .OrderBy(x => x.name);

            foreach (var d in dirs)
            {
                var subCount = Directory.GetDirectories(d.info.FullName).Length;
                var fileCount = Directory.GetFiles(d.info.FullName).Length;
                var label = subCount + fileCount > 0
                    ? $"[cyan]📁 {d.name}[/]  [grey]({subCount} 子目录, {fileCount} 文件)[/]"
                    : $"[cyan]📁 {d.name}[/]";
                root.AddNode(label);
            }

            foreach (var f in files)
            {
                var size = f.info.Length switch
                {
                    < 1024 => $"{f.info.Length} B",
                    < 1024 * 1024 => $"{f.info.Length / 1024.0:F1} KB",
                    _ => $"{f.info.Length / (1024.0 * 1024):F1} MB"
                };
                root.AddNode($"[green]📄 {f.name}[/]  [grey]{size}[/]");
            }

            var totalDirs = dirs.Count();
            var totalFiles = files.Count();
            AnsiConsole.Write(root);
            return ($"[grey]共 {totalDirs} 个目录, {totalFiles} 个文件[/]", true);
        }
        catch (Exception ex)
        {
            return ($"[red]列目录失败:[/] {ex.Message}", true);
        }
    }
}
