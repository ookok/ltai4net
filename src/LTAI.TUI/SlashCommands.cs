using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;
using LTAI.AI;
using LTAI.Core.Configuration;

namespace LTAI.TUI;

/// <summary>
/// Slash command system — ported from DeepSeek-Reasonix slash command pattern.
/// Commands: /help, /new, /model, /status, /retry, /compact, /memory, /cost
/// </summary>
public static class SlashCommands
{
    /// <summary>Reference to the local ONNX embedder for model management commands.</summary>
    public static LTAI.AI.LocalEmbedder? Embedder { get; set; }

    /// <summary>Reference to the LLM router for provider registration.</summary>
    public static LTAI.AI.MultiProviderChatClient? Router { get; set; }

    /// <summary>HTTP client factory for fetching models from provider APIs.</summary>
    public static System.Net.Http.IHttpClientFactory? HttpFactory { get; set; }

    /// <summary>User-defined common-phrase store (injected from DI at startup).</summary>
    public static LTAI.Agent.Snippets.SnippetStore? SnippetStore { get; set; }

    /// <summary>
    /// When set, ChatLayout reads and clears this on the next /snippet use.
    /// Carries the snippet's content to be filled into the input buffer.
    /// </summary>
    public static string? PendingSnippetFill { get; set; }

    /// <summary>P15 hot-editable workflow registry (injected from DI at startup).</summary>
    public static LTAI.Agent.Workflows.YAMLWorkflowRegistry? WorkflowRegistry { get; set; }

    /// <summary>P16.1: Pipes (sequential/concurrent pipeline orchestrator, injected from DI).</summary>
    public static LTAI.Agent.Workflows.AgentWorkflows? Pipes { get; set; }

    /// <summary>Model metadata provider (injected from DI).</summary>
    public static LTAI.AI.ModelMetadataProvider? ModelsProvider { get; set; }

    /// <summary>P14.14: Background job service for /jobs subcommand (list/watch/cancel).</summary>
    public static LTAI.Agent.Tools.BackgroundJobService? Jobs { get; set; }

    /// <summary>Current L1 (fast) model name.</summary>
    public static string? L1Model { get; set; }
    /// <summary>Current L2 (pro) model name.</summary>
    public static string? L2Model { get; set; }
    /// <summary>Current active provider name.</summary>
    public static string? ActiveProvider { get; set; }

    /// <summary>When set by cascade menu, ChatLayout fills this into input buffer.</summary>
    public static string? PendingInput { get; set; }

    // ═══ 级联菜单 ═══
    public static string[] CascadeStack = Array.Empty<string>();
    public static string[] CascadeItems = Array.Empty<string>();
    public static int CascadeSel;
    public static string CascadeCmd = "";
    public static bool InCascadeMenu => CascadeItems.Length > 0;

    public static void OpenCascadeMenu(string cmd, string? args = null)
    {
        CascadeCmd = cmd;
        CascadeStack = args != null ? new[] { args } : Array.Empty<string>();
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
        CascadeStack = Array.Empty<string>();
        CascadeItems = Array.Empty<string>();
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
        var choices = node?.Items ?? Array.Empty<string>();
        CascadeItems = CascadeStack.Length > 0 ? new[] { "← 返回" }.Concat(choices).ToArray() : choices;
        CascadeSel = CascadeStack.Length > 0 ? 1 : 0;
    }

    /// <summary>共享 HttpClient（避免每次 new 导致 socket 泄漏）</summary>
    private static readonly HttpClient _sharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Known LLM providers from KnownKeys + local providers.</summary>
    public static readonly Dictionary<string, ProviderInfo> KnownProviders = BuildKnownProviders();

    public sealed record ProviderInfo(string EnvVar, string Endpoint, string Model);

    private static Dictionary<string, ProviderInfo> BuildKnownProviders()
    {
        var d = LTAI.Core.Configuration.KnownKeys.All
            .Where(k => k.Endpoint != null && k.Model != null)
            .ToDictionary(k => k.Service, k => new ProviderInfo(k.EnvVar, k.Endpoint!, k.Model!));
        d["Ollama"]   = new("", "http://localhost:11434/v1", "llama3.2");
        d["LMStudio"] = new("", "http://localhost:1234/v1",  "local-model");
        d["vLLM"]     = new("", "http://localhost:8000/v1",  "meta-llama/Llama-3.2-3B-Instruct");
        return d;
    }

    private static readonly Dictionary<string, int> UsageCount = new();

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
        new("exit",    "高级",  "退出应用", "quit,q,退出"),
    };

    private static readonly Dictionary<string, SlashSpec> ByName = Commands
        .SelectMany(c => new[] { c.Cmd }.Concat(c.Aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(a => (a, c)))
        .ToDictionary(x => x.a, x => x.c, StringComparer.OrdinalIgnoreCase);

    /// <summary>根据输入前缀返回匹配的命令名（含别名），用于自动联想。</summary>
    public static string[] GetSuggestions(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return [];
        return ByName.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k)
            .ToArray();
    }

    /// <summary>带分组和显示文本的建议项。</summary>
    public sealed record SuggestionItem(
        string DisplayText,   // 显示文本，如 "/memory  [扩展]"
        string Completion,    // 补全文本，如 "/memory"（完整命令名，非别名）
        string Group,         // 分组，如 "扩展"
        bool IsAlias);        // 是否为别名

    /// <summary>返回带分组/别名信息的建议列表，按分组排序。</summary>
    public static List<SuggestionItem> GetSuggestionItems(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return [];

        // 当前缀为 "/" 时返回所有命令
        if (prefix == "/")
        {
            var allItems = new List<SuggestionItem>();
            foreach (var spec in Commands)
            {
                allItems.Add(new SuggestionItem(
                    $"/{spec.Cmd}  [dim]{spec.Group}[/]",
                    $"/{spec.Cmd}",
                    spec.Group,
                    IsAlias: false));
                var aliases = spec.Aliases
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var alias in aliases)
                {
            allItems.Add(new SuggestionItem(
                    $"{alias}  [dim]{spec.Group} → /{spec.Cmd}[/]",
                        $"/{spec.Cmd}",
                        spec.Group,
                        IsAlias: true));
                }
            }
            return allItems;
        }

        // 去掉前导 "/" 进行匹配
        var sp = prefix.StartsWith("/") ? prefix[1..] : prefix;

        // 收集匹配的命令名（主名）
        var matchedCmds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in ByName.Keys)
        {
            if (k.StartsWith(sp, StringComparison.OrdinalIgnoreCase))
                matchedCmds.Add(ByName[k].Cmd);
        }

        // 对每个匹配的主命令，生成 SuggestionItem
        var items = new List<SuggestionItem>();
        foreach (var spec in Commands)
        {
            if (!matchedCmds.Contains(spec.Cmd)) continue;

            // 主名条目
            items.Add(new SuggestionItem(
                $"/{spec.Cmd}  [dim]{spec.Group}[/]",
                $"/{spec.Cmd}",
                spec.Group,
                IsAlias: false));

            // 别名条目（只显示别名本身，补全到主名）
            var aliases = spec.Aliases
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(a => a.Length > 0 && a.StartsWith(sp, StringComparison.OrdinalIgnoreCase));
            foreach (var alias in aliases)
            {
                items.Add(new SuggestionItem(
                    $"{alias}  [dim]{spec.Group} → /{spec.Cmd}[/]",
                    $"/{spec.Cmd}",
                    spec.Group,
                    IsAlias: true));
            }
        }

        // 按分组排序，组内主名在前别名在后
        return items.OrderBy(i => i.Group)
                    .ThenBy(i => i.IsAlias ? 1 : 0)
                    .ThenBy(i => i.Completion)
                    .ToList();
    }

    /// <summary>Show an interactive command picker. Returns full command string or null if cancelled.</summary>
    public static string? ShowPicker()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[bold yellow]📋 LTAI 命令列表[/]\n");

        var flatList = new List<string>(); // index -> cmdName
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

        // Write list then prompt using AnsiConsole.Ask for reliable input
        AnsiConsole.WriteLine();
        AnsiConsole.Markup(sb.ToString());
        var input = AnsiConsole.Ask<string>("[grey]编号:[/]");
        if (string.IsNullOrWhiteSpace(input)) return null;

        if (int.TryParse(input, out var num) && num >= 1 && num <= flatList.Count)
        {
            var cmdName = flatList[num - 1];
            var spec = Commands.FirstOrDefault(c => c.Cmd == cmdName)
                ?? throw new InvalidOperationException($"Command '{cmdName}' not found in command list");

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
        if (!input.StartsWith('/')) return false;

        var parts = input.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmdName = parts[0][1..].ToLowerInvariant();
        // 单独输入 / 或 /help 都显示帮助
        if (string.IsNullOrEmpty(cmdName)) cmdName = "help";
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
        // Commands with sub-options → open cascade menu
        bool hasRoute = CascadeRoutes.Resolve(spec.Cmd, Array.Empty<string>()) != null;
        if (string.IsNullOrWhiteSpace(args) && hasRoute)
        {
            OpenCascadeMenu(spec.Cmd);
            statusMessage = BuildCascadeText();
            return true;
        }

        var (h, s) = spec.Cmd switch
        {
            "help" => Help(),
            "exit" => ("", false),
            "new" => ("Session cleared. Starting fresh.", true),
            "retry" => ("Retrying last message...", true),
            "compact" => ("Summarizing older turns...", true),
            "config" => HandleConfigCommand(args),
            "model" => HandleModelCommand(args),
            "models" => HandleModelsCommand(),
            "snippet" => HandleSnippetCommand(args),
            "workflow" => HandleWorkflowCommand(args),
            "status" => Status(),
            "pipe" => HandlePipeCommand(args),
            "jobs" => HandleJobsCommand(args),
            "cost" => ("Cost tracking: see model provider dashboard", true),
            "lang" => HandleLangCommand(args),
            "skill" => !string.IsNullOrEmpty(args) ? ($"Running skill '{args}'...", true) : ("Skills: use `run_skill` tool", true),
            "mode" => args switch { "review" => ("Edit mode: review", true), "auto" => ("Edit mode: auto", true), _ => ("Usage: /mode review|auto", true) },
            "ls" => ListDir(args),
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

    /// <summary>List L0/L1/L2 current model info + online models.</summary>
    private static (string, bool) HandleModelsCommand()
    {
        var lines = new List<string> { "[bold yellow]当前模型配置[/]\n" };

        // L0: embedding model
        var embedder = Embedder;
        if (embedder != null && embedder.Available)
            lines.Add($"  [cyan]L0 嵌入[/]  {embedder.CurrentModelName}  [dim]({embedder.Dim}d)[/]");
        else if (embedder != null)
            lines.Add("  [cyan]L0 嵌入[/]  [yellow]未加载 (运行 /model l0 download)[/]");
        else
            lines.Add("  [cyan]L0 嵌入[/]  [grey]不可用[/]");

        // L1/L2: read from layers.json
        (string? p, string? m) l1 = (null, null), l2 = (null, null);
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, ".livingtree", "layers.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                string? rp(JsonElement e) => e.TryGetProperty("Provider", out var x) ? x.GetString() : null;
                string? rm(JsonElement e) => e.TryGetProperty("Model", out var x) ? x.GetString() : null;
                if (doc.RootElement.TryGetProperty("l1", out var l1e)) l1 = (rp(l1e), rm(l1e));
                if (doc.RootElement.TryGetProperty("l2", out var l2e)) l2 = (rp(l2e), rm(l2e));
            }
        }
        catch { }
        lines.Add(l1.p != null
            ? $"  [cyan]L1 标准[/]  {l1.p} / [white]{l1.m}[/]"
            : "  [cyan]L1 标准[/]  [yellow]未配置 (/model l1)[/]");
        lines.Add(l2.p != null
            ? $"  [cyan]L2 深度[/]  {l2.p} / [white]{l2.m}[/]"
            : "  [cyan]L2 深度[/]  [yellow]未配置 (/model l2)[/]");

        // Online models
        var mp = ModelsProvider;
        if (mp == null) { lines.Add("\n[grey]ModelMetadataProvider 未注册[/]"); return (string.Join("\n", lines), true); }
        var all = mp.AllModels;
        if (all.Count == 0) { lines.Add("\n[grey]在线模型信息尚未获取（后台刷新进行中）[/]"); return (string.Join("\n", lines), true); }

        lines.Add($"\n[bold yellow]在线模型元数据[/]\n");
        foreach (var group in all.GroupBy(m => m.Provider).OrderBy(g => g.Key))
        {
            lines.Add($"  [cyan]{group.Key}[/][green] ({group.Count()})[/]");

            var sample = group.OrderByDescending(m => m.ContextWindow ?? 0).Take(5);
            foreach (var m in sample)
            {
                var ctx = m.ContextWindow != null ? FormatNum(m.ContextWindow.Value) : "";
                var caps = m.Capabilities != 0
                    ? $" [dim]{AbbrevCaps(m.Capabilities)}[/]"
                    : "";
                var priceIn = m.PriceInPerM > 0 ? $" [yellow]¥{m.PriceInPerM:F2} 输入[/]" : "";
                var priceOut = m.PriceOutPerM > 0 ? $" [yellow]¥{m.PriceOutPerM:F2} 输出[/]" : "";
                var priceCache = m.PriceInCachePerM > 0 ? $" [yellow]¥{m.PriceInCachePerM:F2} 缓存[/]" : "";
                var priceLine = (priceIn + priceOut + priceCache).TrimStart() != ""
                    ? $"\n      {priceIn}{priceOut}{priceCache}"
                    : "";
                lines.Add($"    · [white]{m.Id}[/] {ctx}{caps}{priceLine}");
            }
            var remaining = group.Count() - sample.Count();
            if (remaining > 0)
                lines.Add($"    [dim]...还有 {remaining} 个模型[/]");
        }

        lines.Add($"\n[grey]总计 {all.Count} 个模型，来自 {all.Select(m => m.Provider).Distinct().Count()} 个 Provider[/]");
        lines.Add("[dim]能力缩写: Chat=聊天, Str=流式, Tool=工具, Func=函数, Struct=结构化输出, Vis=视觉, Emb=嵌入, Img=图像[/]");
        return (string.Join("\n", lines), true);
    }

    private static string FormatNum(int n) =>
        n >= 1_000_000 ? $"[grey]{n / 1_000_000}M ctx[/]" :
        n >= 1_000    ? $"[grey]{n / 1_000}K ctx[/]" :
        $"[grey]{n}[/]";

    private static string AbbrevCaps(ModelCapability caps)
    {
        if (caps == 0) return "";
        var parts = new List<string>();
        if (caps.HasFlag(ModelCapability.Chat)) parts.Add("Chat");
        if (caps.HasFlag(ModelCapability.Streaming)) parts.Add("Str");
        if (caps.HasFlag(ModelCapability.ToolCall)) parts.Add("Tool");
        if (caps.HasFlag(ModelCapability.FunctionCall)) parts.Add("Func");
        if (caps.HasFlag(ModelCapability.StructuredOutput)) parts.Add("Struct");
        if (caps.HasFlag(ModelCapability.Vision)) parts.Add("Vis");
        if (caps.HasFlag(ModelCapability.Embedding)) parts.Add("Emb");
        if (caps.HasFlag(ModelCapability.ImageGeneration)) parts.Add("Img");
        return string.Join(", ", parts);
    }

    /// <summary>Persist L1/L2 selection to .livingtree/layers.json.</summary>
    private static void SaveLayerSelection(string layer, string provider, string model)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, ".livingtree");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "layers.json");
            var data = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)) ?? new() : new();
            data[layer] = JsonSerializer.SerializeToElement(new { Provider = provider, Model = model });
            File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }
    /// <summary>Read persisted layer selection; null if not set.</summary>
    private static (string? provider, string? model)? ReadLayerSelection(string layer)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, ".livingtree", "layers.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty(layer, out var el)) return null;
            var p = el.GetProperty("Provider").GetString();
            var m = el.GetProperty("Model").GetString();
            if (!string.IsNullOrEmpty(p) && !string.IsNullOrEmpty(m)) return (p, m);
            return null;
        }
        catch { return null; }
    }

    /// <summary>Handle /model l0|l1|l2|list|download|switch|delete...</summary>
    private static (string, bool) HandleModelCommand(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var subCmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var subArgs = parts.Length > 1 ? parts[1] : "";

        var embedder = Embedder;
        if (embedder == null && subCmd is not "l1" and not "l2" and not "ls")
            return ("ONNX embedder not available", true);

        if (subCmd is "l1" or "l2")
        {
            var layerArgs = subArgs.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (layerArgs.Length == 0)
                return HandleLayerSelect(subCmd);
            if (layerArgs.Length == 1)
                return HandleLayerSelectComplete(subCmd, layerArgs[0]);
            // Save + register
            var p = layerArgs[0]; var m = layerArgs[1];
            SaveLayerSelection(subCmd, p, m);
            if (subCmd == "l1") L1Model = m; else L2Model = m;
            if (Router != null && KnownProviders.TryGetValue(p, out var info))
            {
                var key = SecretManager.Get(info.EnvVar) ?? "";
                Router.Register(subCmd, OpenAIChatClientFactory.Create(info.Endpoint ?? "", m, key));
            }
            return ($"[green]✓ {subCmd.ToUpperInvariant()}={p}/{m}[/]", true);
        }

        if (subCmd == "l0" && subArgs.StartsWith("api", StringComparison.OrdinalIgnoreCase))
        {
            var apiArgs = subArgs.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            return apiArgs.Length == 1 ? ShowL0ApiProviders() : ShowL0ApiModels(apiArgs[1]);
        }

        // L0 / legacy embedder commands
        if (subCmd == "l0")
            subCmd = subArgs.Split(' ', 2)[0].ToLowerInvariant();
        subArgs = subArgs.Trim();

        return (subCmd, embedder) switch
        {
            ("" or "list", not null) => HandleModelList(embedder),
            ("switch", not null) => HandleModelSwitch(embedder, subArgs),
            ("download", not null) => HandleModelDownload(embedder, subArgs),
            ("delete" or "remove", not null) => HandleModelDelete(embedder, subArgs),
            ("cleanup", not null) => HandleModelCleanup(embedder, subArgs),
            ("info", not null) => HandleModelInfo(embedder),
            ("quant", not null) => HandleModelQuant(embedder, subArgs),
            _ => ("用法: /model [l0 [list|download|switch|delete|info|cleanup|quant]] | l1 | l2", true),
        };
    }

    /// <summary>Interactive L1/L2 provider + model selection wizard.</summary>
    private static (string, bool) HandleLayerSelect(string layer)
    {
        var providers = KnownProviders
            .Where(kv => !string.IsNullOrEmpty(kv.Value.EnvVar))
            .Select(kv => kv.Key)
            .ToList();
        if (providers.Count == 0)
            return ("没有可用 provider。请先设置 API Key。", true);

        // Step 1: pick provider
        var picker = new SelectionPrompt<string>()
            .Title($"[yellow]选择 {layer.ToUpperInvariant()} provider:[/]")
            .PageSize(15)
            .AddChoices(providers);
        var chosen = AnsiConsole.Prompt(picker);
        if (!KnownProviders.TryGetValue(chosen, out var info)) return ("", true);

        // Step 2: check / set API key.
        // Local providers (Ollama/LMStudio/vLLM) have no EnvVar → no key needed.
        string? existingKey = null;
        if (!string.IsNullOrEmpty(info.EnvVar))
        {
            existingKey = SecretManager.Get(info.EnvVar);
            if (string.IsNullOrEmpty(existingKey) && layer == "l2")
            {
                var l1Sel = ReadLayerSelection("l1");
                if (l1Sel is { provider: not null } && string.Equals(l1Sel.Value.provider, chosen, StringComparison.OrdinalIgnoreCase))
                    existingKey = SecretManager.Get(info.EnvVar);
            }
            if (string.IsNullOrEmpty(existingKey))
            {
                var key = AnsiConsole.Prompt(new TextPrompt<string>($"[yellow]输入 {chosen} API Key ({info.EnvVar}):[/]").Secret());
                if (string.IsNullOrWhiteSpace(key)) return ("已取消", true);
                existingKey = key;
                SecretManager.Set(info.EnvVar, key, persistent: true);
            }
        }

        // Step 3: fetch models from API
        var authHeader = existingKey != null ? new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", existingKey) : null;
        AnsiConsole.Status().Start("获取模型列表...", ctx =>
        {
            try
            {
                var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var req = new HttpRequestMessage(HttpMethod.Get, $"{info.Endpoint.TrimEnd('/')}/models");
                if (authHeader != null) req.Headers.Authorization = authHeader;
                var resp = http.Send(req);
                if (!resp.IsSuccessStatusCode) { _availableLayerModels = null; return; }
                using var json = JsonDocument.Parse(resp.Content.ReadAsStream());
                _availableLayerModels = json.RootElement.GetProperty("data")
                    .EnumerateArray().Select(m => m.GetProperty("id").GetString() ?? "")
                    .Where(id => !string.IsNullOrEmpty(id)).OrderBy(id => id).ToList();
            }
            catch { _availableLayerModels = null; }
        });
        if (_availableLayerModels is not { Count: > 0 })
            return ("获取模型列表失败，请检查 API Key 和网络连接", true);

        // Step 4: pick model
        var modelPicker = new SelectionPrompt<string>()
            .Title($"[yellow]为 {layer.ToUpperInvariant()} 选择模型 ({chosen}):[/]")
            .PageSize(15)
            .AddChoices(_availableLayerModels);
        var model = AnsiConsole.Prompt(modelPicker);
        _availableLayerModels = null;

        // Step 5: persist selection
        SaveLayerSelection(layer, chosen, model);
        if (layer == "l1") L1Model = model; else L2Model = model;

        // Step 6: apply to runtime router
        if (Router != null)
        {
            try
            {
                var client = OpenAIChatClientFactory.Create(info.Endpoint, model, existingKey);
                Router.Register(layer, client);
                AnsiConsole.MarkupLine($"[green]✓ 已注册 {layer}: {chosen}/{model}[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]注册失败: {ex.Message.EscapeMarkup()}[/]");
            }
        }

        return ($"已配置 [green]{layer.ToUpperInvariant()}[/] = [cyan]{chosen}[/] / [yellow]{model}[/]", true);
    }
    private static List<string>? _availableLayerModels;

    // ═══════════════════════════════════════════
    //  /model l0 api
    // ═══════════════════════════════════════════

    private static (string, bool) ShowL0ApiProviders()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[bold yellow]/model l0 api[/]");
        sb.AppendLine("[grey]可用 Provider:[/]");
        foreach (var (name, info) in KnownProviders)
        {
            if (string.IsNullOrEmpty(info.Endpoint)) continue;
            var hasKey = !string.IsNullOrEmpty(info.EnvVar) && SecretManager.Has(info.EnvVar);
            sb.AppendLine($"  · [cyan]{name}[/] {(hasKey ? "[green]Key✓[/]" : "[yellow]需设置 Key[/]")}");
        }
        sb.AppendLine($"[dim]输入 /model l0 api <provider> 继续[/]");
        return (sb.ToString(), true);
    }

    private static (string, bool) ShowL0ApiModels(string provider)
    {
        if (!KnownProviders.TryGetValue(provider, out var info))
            return ($"未知 provider: {provider}", true);

        var sb = new StringBuilder();
        sb.AppendLine($"[bold yellow]L0 Embedding: {provider}[/]");

        string? key = !string.IsNullOrEmpty(info.EnvVar) ? SecretManager.Get(info.EnvVar) : null;
        if (!string.IsNullOrEmpty(info.EnvVar) && string.IsNullOrEmpty(key))
        {
            sb.AppendLine($"[red]未设置 {info.EnvVar}，请先 /config apikey {provider}[/]");
            return (sb.ToString(), true);
        }
        sb.AppendLine(key != null ? "[green]✓ Key 已设置[/]" : "[green]✓ 无需 Key[/]");

        sb.Append("[grey]获取 embedding 模型...[/]");
        List<string> models;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var req = new HttpRequestMessage(HttpMethod.Get, $"{info.Endpoint.TrimEnd('/')}/models");
            if (key != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var resp = http.Send(req);
            if (!resp.IsSuccessStatusCode) return ($"获取失败 ({(int)resp.StatusCode})", true);
            using var json = JsonDocument.Parse(resp.Content.ReadAsStream());
            var allModels = json.RootElement.GetProperty("data")
                .EnumerateArray().Select(m => m.GetProperty("id").GetString() ?? "")
                .Where(id => !string.IsNullOrEmpty(id)).OrderBy(id => id).ToList();
            models = allModels.Where(id => id.Contains("embed", StringComparison.OrdinalIgnoreCase)).ToList();
            if (models.Count == 0) models = allModels.Take(20).ToList();
        }
        catch (Exception ex) { return ($"获取失败: {ex.Message.EscapeMarkup()}", true); }

        sb.AppendLine($"[grey]{models.Count} 个模型:[/]");
        foreach (var m in models) sb.AppendLine($"  · [cyan]{m}[/]");
        sb.AppendLine($"[dim]输入 /model l0 api {provider} <模型名> 完成设置[/]");
        return (sb.ToString(), true);
    }

    private static (string, bool) HandleLayerSelectComplete(string layer, string provider)
    {
        if (!KnownProviders.TryGetValue(provider, out var info))
            return ($"未知 provider: {provider}", true);
        var sb = new StringBuilder();
        sb.AppendLine($"[bold]{layer.ToUpperInvariant()} Provider: {provider}[/]");
        string? key = !string.IsNullOrEmpty(info.EnvVar) ? SecretManager.Get(info.EnvVar) : null;
        if (!string.IsNullOrEmpty(info.EnvVar) && string.IsNullOrEmpty(key))
            return ($"未设置 {info.EnvVar}，/config apikey {provider}", true);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var req = new HttpRequestMessage(HttpMethod.Get, $"{info.Endpoint!.TrimEnd('/')}/models");
            if (key != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var resp = http.Send(req);
            if (!resp.IsSuccessStatusCode) return ($"获取模型失败 ({(int)resp.StatusCode})", true);
            using var json = JsonDocument.Parse(resp.Content.ReadAsStream());
            var models = json.RootElement.GetProperty("data")
                .EnumerateArray().Select(m => m.GetProperty("id").GetString() ?? "")
                .Where(id => !string.IsNullOrEmpty(id)).OrderBy(id => id).Take(30).ToList();
            sb.AppendLine($"模型 ({models.Count}):");
            foreach (var m in models) sb.AppendLine($"  · [cyan]{m}[/]");
            sb.AppendLine($"[dim]/model {layer} {provider} <模型名>[/]");
        }
        catch (Exception ex) { sb.AppendLine($"[red]{ex.Message.EscapeMarkup()}[/]"); }
        return (sb.ToString(), true);
    }

    private static (string, bool) HandleModelList(LTAI.AI.LocalEmbedder embedder)
    {
        var models = LTAI.AI.LocalEmbedder.ListAvailableModels();
        if (models.Count == 0) return ("没有可用的 ONNX 模型", true);

        var lines = new List<string> { "[bold yellow]可用的 ONNX Embedding 模型[/]\n" };
        foreach (var m in models)
        {
            var status = m.Downloaded
                ? (string.Equals(m.Id, embedder.CurrentModelName, StringComparison.OrdinalIgnoreCase)
                    ? "[green]● 当前使用[/]"
                    : "[grey]已下载[/]")
                : "[yellow]未下载[/]";
            lines.Add($"  [cyan]{m.Id,-16}[/] {status}");
            lines.Add($"    {m.DisplayName,-20} [grey]{m.Description}[/]");
            lines.Add("");
        }
        return (string.Join("\n", lines), true);
    }

    private static (string, bool) HandleModelSwitch(LTAI.AI.LocalEmbedder embedder, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ("用法: /model switch <模型ID>  例如: /model switch bge-small-zh", true);

        if (!LTAI.AI.LocalEmbedder.KnownModels.ContainsKey(name))
            return ($"未知模型 '{name}'。可用模型: {string.Join(", ", LTAI.AI.LocalEmbedder.KnownModels.Keys)}", true);

        if (!embedder.SwitchModel(name))
        {
            var baseDir = LTAI.AI.LocalEmbedder.BaseModelsDirectory;
            return ($"模型 '{name}' 未下载。请先运行 /model download {name}。模型目录: {baseDir}", true);
        }

        // P14.8: SwitchModel fires ModelSwitched which clears ToolEmbeddingCache,
        // AgentRegistry, ToolRegistry in-process. TUI has no direct access to
        // cache entry counts, but log a hint that downstream caches were reset.
        return ($"已切换到 ONNX 模型: [green]{name}[/]（{LTAI.AI.LocalEmbedder.KnownModels[name].DisplayName}）\n" +
                "已自动清空 tool/agent embedding 缓存，下次路由会重新计算向量。", true);
    }

    private static (string, bool) HandleModelDownload(LTAI.AI.LocalEmbedder embedder, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            var models = LTAI.AI.LocalEmbedder.ListAvailableModels();
            var pending = models.Where(m => !m.Downloaded).ToList();
            if (pending.Count == 0) return ("所有模型均已下载", true);
            name = pending[0].Id;
        }

        if (!LTAI.AI.LocalEmbedder.KnownModels.ContainsKey(name))
            return ($"未知模型 '{name}'。可用: {string.Join(", ", LTAI.AI.LocalEmbedder.KnownModels.Keys)}", true);

        var info = LTAI.AI.LocalEmbedder.KnownModels[name];

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var success = Task.Run(() => embedder.DownloadModelAsync(name, http)).GetAwaiter().GetResult();
        return success
            ? ($"✅ 模型 [green]{name}[/]（{info.DisplayName}）下载完成", true)
            : ($"❌ 模型 '{name}' 下载失败。请检查网络连接后重试", true);
    }

    private static (string, bool) HandleModelDelete(LTAI.AI.LocalEmbedder embedder, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ("用法: /model delete <模型ID>", true);

        if (!LTAI.AI.LocalEmbedder.KnownModels.ContainsKey(name))
            return ($"未知模型 '{name}'", true);

        if (string.Equals(name, embedder.CurrentModelName, StringComparison.OrdinalIgnoreCase))
            return ($"不能删除当前正在使用的模型 '{name}'。请先切换到其他模型", true);

        if (embedder.DeleteModel(name))
            return ($"已删除模型 '{name}'", true);

        return ($"模型 '{name}' 不存在或已删除", true);
    }

    // ═══════════════════════════════════════════
    //  P14.3: /model cleanup [name] — delete stale on-disk variant
    // ═══════════════════════════════════════════

    private static (string, bool) HandleModelCleanup(LTAI.AI.LocalEmbedder embedder, string arg)
    {
        var baseDir = LTAI.AI.LocalEmbedder.BaseModelsDirectory;
        if (baseDir == null) return ("Models 目录未初始化", true);

        var names = string.IsNullOrWhiteSpace(arg)
            ? LTAI.AI.LocalEmbedder.ListAvailableModels()
                .Where(m => m.Downloaded || m.QuantizedDownloaded)
                .Select(m => m.Id).ToList()
            : new List<string> { arg.Trim() };

        if (names.Count == 0) return ("没有已下载的模型可清理", true);

        int totalFiles = 0;
        long totalBytes = 0;
        var details = new List<string>();
        var currentPref = (LTAI.AI.LocalEmbedder.Options.Quantization ?? "auto").ToLowerInvariant();

        foreach (var name in names)
        {
            if (!LTAI.AI.LocalEmbedder.KnownModels.ContainsKey(name))
            {
                details.Add($"  [red]✗[/] {name}: 未知模型");
                continue;
            }
            var info = LTAI.AI.LocalEmbedder.KnownModels[name];
            var modelDir = Path.Combine(baseDir, name);
            if (!Directory.Exists(modelDir))
            {
                details.Add($"  [grey]–[/] {name}: 未下载，跳过");
                continue;
            }

            bool targetQuant = currentPref switch
            {
                "fp32" => false,
                "int8" => true,
                _ => string.Equals(embedder.CurrentModelName, name, StringComparison.OrdinalIgnoreCase)
                        ? embedder.UsingQuantizedModel
                        : true,
            };

            long bytesRemoved = 0;
            int filesRemoved = 0;
            if (targetQuant)
            {
                var fp32 = Path.Combine(modelDir, "model.onnx");
                if (File.Exists(fp32) && new FileInfo(fp32).Length > 1024)
                {
                    bytesRemoved += new FileInfo(fp32).Length;
                    try { File.Delete(fp32); filesRemoved++; } catch { }
                }
            }
            else
            {
                if (info.QuantizedFileName != null)
                {
                    var q = Path.Combine(modelDir, info.QuantizedFileName);
                    if (File.Exists(q))
                    {
                        bytesRemoved += new FileInfo(q).Length;
                        try { File.Delete(q); filesRemoved++; } catch { }
                    }
                }
            }
            totalFiles += filesRemoved;
            totalBytes += bytesRemoved;
            var kept = targetQuant ? "INT8" : "FP32";
            if (filesRemoved > 0)
                details.Add($"  [green]✓[/] {name}: 释放 {FormatBytes(bytesRemoved)}, 保留 {kept}");
            else
                details.Add($"  [grey]–[/] {name}: 已单变种（保留 {kept}）");
        }

        var header = "[bold yellow]模型清理[/]\n";
        if (totalFiles > 0)
            header += $"  释放 [green]{FormatBytes(totalBytes)}[/]（{totalFiles} 文件）\n\n";
        else
            header += "  没有可清理的旧变种\n\n";
        return (header + string.Join("\n", details), true);
    }

    // ═══════════════════════════════════════════
    //  P14.3: /model info — detailed per-model table
    // ═══════════════════════════════════════════

    private static (string, bool) HandleModelInfo(LTAI.AI.LocalEmbedder embedder)
    {
        var baseDir = LTAI.AI.LocalEmbedder.BaseModelsDirectory;
        var models = LTAI.AI.LocalEmbedder.ListAvailableModels();
        var lines = new List<string> { "[bold yellow]ONNX Embedder 详情[/]\n" };

        lines.Add($"  偏好 quant: [cyan]{LTAI.AI.LocalEmbedder.Options.Quantization}[/]  " +
                  $"GPU: [cyan]{LTAI.AI.LocalEmbedder.Options.Gpu}[/]  " +
                  $"DeviceId: [cyan]{LTAI.AI.LocalEmbedder.Options.DeviceId}[/]");
        // P14.9: per-model overrides
        if (LTAI.AI.LocalEmbedder.Options.Models is { Count: > 0 } perModel)
        {
            var entries = string.Join(", ",
                perModel.Select(kv => $"[cyan]{kv.Key}[/]=[yellow]{kv.Value}[/]"));
            lines.Add($"  per-model: {entries}");
        }
        if (LTAI.AI.LocalEmbedder.DefaultDisabled)
            lines.Add("  状态: [grey]已禁用（远程 API 接管）[/]");
        else if (embedder.Available)
        {
            var ep = embedder.ActiveExecutionProvider ?? "?";
            var epColor = ep == "CPU" ? "grey" : "green";
            var quant = embedder.UsingQuantizedModel ? "INT8" : "FP32";
            var quantColor = quant == "INT8" ? "green" : "yellow";
            lines.Add($"  当前: [cyan]{embedder.CurrentModelName}[/]  " +
                      $"EP: [{epColor}]{ep}[/]  " +
                      $"quant: [{quantColor}]{quant}[/]  " +
                      $"Dim: [cyan]{embedder.Dim}[/]");
        }
        else
            lines.Add("  状态: [yellow]未加载（运行 /model list|download）[/]");
        lines.Add($"  目录: [grey]{baseDir ?? "(not set)"}[/]\n");

        foreach (var m in models)
        {
            var isCurrent = string.Equals(m.Id, embedder.CurrentModelName, StringComparison.OrdinalIgnoreCase);
            var marker = isCurrent ? "[green]●[/]" : " ";
            lines.Add($"  {marker} [cyan]{m.Id,-16}[/]  {m.DisplayName}");
            lines.Add($"    [grey]{m.Description}[/]");

            var modelDir = baseDir != null ? Path.Combine(baseDir, m.Id) : null;
            if (modelDir != null && Directory.Exists(modelDir))
            {
                var fp32File = Path.Combine(modelDir, "model.onnx");
                long fp32Size = 0;
                var fp32Valid = false;
                if (File.Exists(fp32File))
                {
                    fp32Size = new FileInfo(fp32File).Length;
                    fp32Valid = fp32Size > 1024;
                }
                var fp32Mark = fp32Valid ? "[green]●[/]" : "[grey]○[/]";
                lines.Add($"    FP32: {fp32Mark} {(fp32Valid ? FormatBytes(fp32Size) : "—")}");

                var qInfo = LTAI.AI.LocalEmbedder.KnownModels[m.Id];
                if (qInfo.QuantizedFileName != null)
                {
                    var qFile = Path.Combine(modelDir, qInfo.QuantizedFileName);
                    var qValid = File.Exists(qFile) && new FileInfo(qFile).Length > 1024;
                    var qMark = qValid ? "[green]●[/]" : "[grey]○[/]";
                    lines.Add($"    INT8: {qMark} {(qValid ? FormatBytes(new FileInfo(qFile).Length) : "—")}");
                }
                else
                    lines.Add("    INT8: [grey](无上游量化版)[/]");

                var vocab = Path.Combine(modelDir, "vocab.txt");
                if (File.Exists(vocab))
                    lines.Add($"    Vocab: [green]●[/] {FormatBytes(new FileInfo(vocab).Length)}");
                else
                    lines.Add("    Vocab: [red]○[/] —");

                // P14.9: effective quant preference for this model
                var effQuant = LTAI.AI.LocalEmbedder.Options.GetQuantizationFor(m.Id);
                var hasOverride = LTAI.AI.LocalEmbedder.Options.Models.ContainsKey(m.Id);
                var effColor = effQuant == "int8" || effQuant == "auto" ? "green" : "yellow";
                var suffix = hasOverride ? " (override)" : "";
                lines.Add($"    Eff. quant: [{effColor}]{effQuant}[/]{suffix}");
            }
            else
                lines.Add("    [yellow](未下载)[/]");
            lines.Add("");
        }
        return (string.Join("\n", lines), true);
    }

    // ═══════════════════════════════════════════
    //  P14.3: /model quant <fp32|int8|auto> — toggle global quantization preference
    // ═══════════════════════════════════════════

    private static (string, bool) HandleModelQuant(LTAI.AI.LocalEmbedder embedder, string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return ($"当前 quant 偏好: [cyan]{LTAI.AI.LocalEmbedder.Options.Quantization}[/]\n" +
                    $"用法: /model quant fp32|int8|auto", true);

        var val = arg.Trim().ToLowerInvariant();
        if (val != "fp32" && val != "int8" && val != "auto")
            return ($"未知 quant 偏好: '{arg}'。可用: fp32|int8|auto", true);

        var oldVal = LTAI.AI.LocalEmbedder.Options.Quantization;
        LTAI.AI.LocalEmbedder.Options.Quantization = val;

        var msg = $"Quant 偏好: [grey]{oldVal}[/] → [green]{val}[/]\n";

        if (LTAI.AI.LocalEmbedder.DefaultDisabled)
        {
            msg += "（embedder 已禁用，下次启动生效）";
            return (msg, true);
        }

        if (embedder.CurrentModelName != null)
        {
            try
            {
                if (embedder.SwitchModel(embedder.CurrentModelName))
                {
                    var newQuant = embedder.UsingQuantizedModel ? "INT8" : "FP32";
                    var qColor = newQuant == "INT8" ? "green" : "yellow";
                    msg += $"已重新加载 [cyan]{embedder.CurrentModelName}[/] (使用 [{qColor}]{newQuant}[/])";
                }
                else
                {
                    msg += $"[yellow]⚠[/] 重新加载失败 — 目标 {val} 的变种不存在。\n" +
                            $"    提示: /model info 看磁盘状态，/model download {embedder.CurrentModelName} 重下";
                }
            }
            catch (Exception ex)
            {
                msg += $"[yellow]⚠[/] 重新加载异常: {ex.Message}";
            }
        }
        else
        {
            msg += "（无活动模型，下次启动生效）";
        }
        return (msg, true);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    // ═══════════════════════════════════════════
    //  /snippet commands — user-defined common phrases
    // ═══════════════════════════════════════════

    private static (string, bool) HandleSnippetCommand(string args)
    {
        var store = SnippetStore;
        if (store == null)
            return ("常用语存储未初始化", true);

        // Fallback: /snippet <key> with no recognized subcommand → treat as `use <key>`
        var cmd = LTAI.Agent.Snippets.SnippetCommandParser.Parse(args);
        if (cmd.Action == LTAI.Agent.Snippets.SnippetAction.Unknown)
        {
            var firstToken = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (!string.IsNullOrEmpty(firstToken))
            {
                var existing = store.GetAsync(firstToken).GetAwaiter().GetResult();
                if (existing != null)
                    cmd = new LTAI.Agent.Snippets.SnippetCommand(
                        LTAI.Agent.Snippets.SnippetAction.Use, firstToken, "", "", null);
            }
        }
        if (cmd.Error != null)
            return ($"[red]{cmd.Error}[/]", true);

        return cmd.Action switch
        {
            LTAI.Agent.Snippets.SnippetAction.List => SnippetList(store),
            LTAI.Agent.Snippets.SnippetAction.Save => SnippetSave(store, cmd),
            LTAI.Agent.Snippets.SnippetAction.Use => SnippetUse(store, cmd),
            LTAI.Agent.Snippets.SnippetAction.Delete => SnippetDelete(store, cmd),
            LTAI.Agent.Snippets.SnippetAction.Rename => SnippetRename(store, cmd),
            LTAI.Agent.Snippets.SnippetAction.Edit => SnippetSave(store,
                new LTAI.Agent.Snippets.SnippetCommand(
                    LTAI.Agent.Snippets.SnippetAction.Save, cmd.Key, "", cmd.Content, null)),
            _ => ($"未知子命令。用法: /snippet list|save|use|edit|rename|delete", true),
        };
    }

    private static (string, bool) SnippetList(LTAI.Agent.Snippets.SnippetStore store)
    {
        var list = store.ListAsync().GetAwaiter().GetResult();
        if (list.Count == 0)
            return ("[yellow]暂无常用语[/]  用法: /snippet save <key> <text>", true);

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Key");
        table.AddColumn("描述");
        table.AddColumn("长度");
        table.AddColumn("使用");
        table.AddColumn("上次使用");

        foreach (var s in list)
        {
            var lastUsed = s.LastUsedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "[grey]从未[/]";
            var desc = string.IsNullOrEmpty(s.Description) ? "[grey]—[/]" : s.Description.EscapeMarkup();
            table.AddRow(
                $"[cyan]{s.Key.EscapeMarkup()}[/]",
                desc,
                $"{s.Content.Length}",
                s.UseCount > 0 ? $"[green]{s.UseCount}[/]" : "[grey]0[/]",
                lastUsed);
        }

        AnsiConsole.Write(table);
        return ($"[grey]共 {list.Count} 条[/]", true);
    }

    private static (string, bool) SnippetSave(LTAI.Agent.Snippets.SnippetStore store,
        LTAI.Agent.Snippets.SnippetCommand cmd)
    {
        try
        {
            store.SaveAsync(new LTAI.Agent.Snippets.Snippet
            {
                Key = cmd.Key,
                Content = cmd.Content,
                Description = "",
            }).GetAwaiter().GetResult();
            return ($"[green]✅ 已保存常用语[/] [cyan]/{cmd.Key}[/] ({cmd.Content.Length} 字符)", true);
        }
        catch (ArgumentException ex)
        {
            return ($"[red]❌ {ex.Message}[/]", true);
        }
    }

    private static (string, bool) SnippetUse(LTAI.Agent.Snippets.SnippetStore store,
        LTAI.Agent.Snippets.SnippetCommand cmd)
    {
        var snippet = store.GetAsync(cmd.Key).GetAwaiter().GetResult();
        if (snippet == null)
            return ($"[red]❌ 找不到常用语 '/{cmd.Key}'[/]。输入 /snippet list 查看", true);

        store.TouchAsync(cmd.Key).GetAwaiter().GetResult();
        // Set pending fill; ChatLayout will read and clear this on next input cycle
        PendingSnippetFill = snippet.Content;
        return ($"[green]✅ 已调出常用语[/] [cyan]/{snippet.Key}[/]（{snippet.Content.Length} 字符）。已填入输入框", true);
    }

    private static (string, bool) SnippetDelete(LTAI.Agent.Snippets.SnippetStore store,
        LTAI.Agent.Snippets.SnippetCommand cmd)
    {
        var existing = store.GetAsync(cmd.Key).GetAwaiter().GetResult();
        if (existing == null)
            return ($"[red]❌ 找不到常用语 '/{cmd.Key}'[/]", true);

        var usedHint = existing.UseCount > 0
            ? $" [yellow]（已使用 {existing.UseCount} 次）[/]"
            : "";
        var ok = store.DeleteAsync(cmd.Key).GetAwaiter().GetResult();
        return ok
            ? ($"[green]✅ 已删除常用语[/] [cyan]/{cmd.Key}[/]{usedHint}", true)
            : ($"[red]❌ 删除失败[/]", true);
    }

    private static (string, bool) SnippetRename(LTAI.Agent.Snippets.SnippetStore store,
        LTAI.Agent.Snippets.SnippetCommand cmd)
    {
        try
        {
            var ok = store.RenameAsync(cmd.Key, cmd.NewKey).GetAwaiter().GetResult();
            return ok
                ? ($"[green]✅ 已重命名[/] [cyan]/{cmd.Key}[/] → [cyan]/{cmd.NewKey}[/]", true)
                : ($"[red]❌ 找不到常用语 '/{cmd.Key}'[/]", true);
        }
        catch (InvalidOperationException ex)
        {
            return ($"[red]❌ {ex.Message}[/]", true);
        }
        catch (ArgumentException ex)
        {
            return ($"[red]❌ {ex.Message}[/]", true);
        }
    }

    // ═══════════════════════════════════════════
    //  /workflow commands — hot-editable YAML/JSON workflows (P15)
    // ═══════════════════════════════════════════

    private static (string, bool) HandleWorkflowCommand(string args)
    {
        var registry = WorkflowRegistry;
        if (registry == null)
            return ("Workflow registry not initialized (YAMLWorkflowRegistry missing in DI)", true);

        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var subArgs = parts.Length > 1 ? parts[1].Trim() : "";

        return sub switch
        {
            "" or "list" => WorkflowList(registry),
            "reload" => WorkflowReload(registry, subArgs),
            "show" => WorkflowShow(registry, subArgs),
            "open" => WorkflowOpen(registry, subArgs),
            _ => ("用法: /workflow list | reload [name|*] | show <name> | open [name]", true),
        };
    }

    private static (string, bool) WorkflowList(LTAI.Agent.Workflows.YAMLWorkflowRegistry registry)
    {
        var list = registry.List();
        if (list.Count == 0)
            return ($"[yellow]暂无 workflow[/]  目录: {registry.WatchDirectory}\n" +
                    "把 *.yaml / *.json 丢进该目录即可热加载", true);

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Name");
        table.AddColumn("Type");
        table.AddColumn("V");
        table.AddColumn("Size");
        table.AddColumn("Loaded");
        table.AddColumn("Path");

        foreach (var w in list)
        {
            var size = w.SizeBytes switch
            {
                < 1024 => $"{w.SizeBytes} B",
                < 1024 * 1024 => $"{w.SizeBytes / 1024.0:F1} KB",
                _ => $"{w.SizeBytes / (1024.0 * 1024):F1} MB"
            };
            var loaded = w.LoadedAtUtc.ToLocalTime().ToString("HH:mm:ss");
            var fileName = System.IO.Path.GetFileName(w.FilePath);
            table.AddRow(
                $"[cyan]{w.Name.EscapeMarkup()}[/]",
                $"[grey]{w.Type.EscapeMarkup()}[/]",
                w.Version.ToString(),
                size,
                loaded,
                $"[grey]{fileName.EscapeMarkup()}[/]");
        }

        AnsiConsole.Write(table);
        return ($"[grey]共 {list.Count} 个 workflow · 目录: {registry.WatchDirectory}[/]", true);
    }

    private static (string, bool) WorkflowReload(LTAI.Agent.Workflows.YAMLWorkflowRegistry registry, string name)
    {
        try
        {
            if (string.IsNullOrEmpty(name) || name == "*")
            {
                var all = registry.List();
                registry.ReloadAllAsync().GetAwaiter().GetResult();
                return ($"[green]✅ 已触发重载[/]  {all.Count} 个 workflow", true);
            }

            // Find the file by name (registry keys are file stems).
            var dir = registry.WatchDirectory;
            var exts = new[] { ".yaml", ".yml", ".json" };
            string? matchPath = null;
            foreach (var ext in exts)
            {
                var p = System.IO.Path.Combine(dir, name + ext);
                if (System.IO.File.Exists(p)) { matchPath = p; break; }
            }
            if (matchPath == null)
                return ($"[red]❌ 找不到 workflow '{name}'[/]  目录: {dir}", true);

            registry.ReloadFileAsync(matchPath).GetAwaiter().GetResult();
            return ($"[green]✅ 已重载[/] [cyan]/{name}[/]", true);
        }
        catch (Exception ex)
        {
            return ($"[red]❌ 重载失败:[/] {ex.Message}", true);
        }
    }

    private static (string, bool) WorkflowShow(LTAI.Agent.Workflows.YAMLWorkflowRegistry registry, string name)
    {
        if (string.IsNullOrEmpty(name))
            return ("用法: /workflow show <name>", true);

        var dir = registry.WatchDirectory;
        var exts = new[] { ".yaml", ".yml", ".json" };
        string? matchPath = null;
        foreach (var ext in exts)
        {
            var p = System.IO.Path.Combine(dir, name + ext);
            if (System.IO.File.Exists(p)) { matchPath = p; break; }
        }
        if (matchPath == null)
            return ($"[red]❌ 找不到 workflow '{name}'[/]  目录: {dir}", true);

        try
        {
            var content = System.IO.File.ReadAllText(matchPath);
            // Show first 60 lines, escape markup
            var lines = content.Split('\n');
            var preview = string.Join("\n", lines.Take(60));
            var truncated = lines.Length > 60 ? $"\n[grey]... ({lines.Length - 60} more lines)[/]" : "";
            AnsiConsole.Write(new Panel(new Markup(preview.EscapeMarkup()))
                .Header($"[green] {name} ({lines.Length} lines) [/]")
                .Border(BoxBorder.Rounded)
                .Expand());
            return ($"[grey]{matchPath}[/]{truncated}", true);
        }
        catch (Exception ex)
        {
            return ($"[red]❌ 读取失败:[/] {ex.Message}", true);
        }
    }

    private static (string, bool) WorkflowOpen(LTAI.Agent.Workflows.YAMLWorkflowRegistry registry, string name)
    {
        if (string.IsNullOrEmpty(name))
            return ("用法: /workflow open <name>  (用系统默认程序打开文件)", true);

        var dir = registry.WatchDirectory;
        var exts = new[] { ".yaml", ".yml", ".json" };
        string? matchPath = null;
        foreach (var ext in exts)
        {
            var p = System.IO.Path.Combine(dir, name + ext);
            if (System.IO.File.Exists(p)) { matchPath = p; break; }
        }
        if (matchPath == null)
            return ($"[red]❌ 找不到 workflow '{name}'[/]  目录: {dir}", true);

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = matchPath,
                UseShellExecute = true,  // open with default OS app
            });
            return ($"[green]✅ 已用系统默认程序打开[/] [cyan]{matchPath}[/]", true);
        }
        catch (Exception ex)
        {
            return ($"[red]❌ 打开失败:[/] {ex.Message}", true);
        }
    }

    // ═══════════════════════════════════════════
    //  /config commands — LLM provider/model/apikey
    // ═══════════════════════════════════════════

    private static (string, bool) HandleConfigCommand(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var subCmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var subArgs = parts.Length > 1 ? parts[1] : "";

        return subCmd switch
        {
            "status" => ConfigStatus(),
            "provider" => ConfigSelectProvider(),
            "apikey" => ConfigSetApiKey(subArgs),
            "key" => ConfigSetApiKey(subArgs),
            "l1" => ConfigSelectModel("l1"),
            "l2" => ConfigSelectModel("l2"),
            "model" => ConfigSelectModel(subArgs),
            "export" => ConfigExport(subArgs),
            "import" => ConfigImport(subArgs),
            _ => ("用法: /config status|provider|apikey|l1|l2|export [file]|import [file]", true),
        };
    }

    private static (string, bool) ConfigStatus()
    {
        var prov = ActiveProvider ?? "未设置";
        var l1 = L1Model ?? "未设置";
        var l2 = L2Model ?? "未设置";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[bold yellow]LLM 配置状态[/]\n");
        sb.AppendLine($"[bold]当前 Provider:[/] [cyan]{prov}[/]");
        sb.AppendLine($"[bold]L1 模型 (Fast):[/] {l1}");
        sb.AppendLine($"[bold]L2 模型 (Pro):[/]  {l2}");
        sb.AppendLine();

        sb.AppendLine("[bold]可用 Provider:[/]");
        foreach (var (name, info) in KnownProviders)
        {
            var keyStatus = string.IsNullOrEmpty(info.EnvVar)
                ? "[dim]Local[/]"
                : LTAI.Core.Configuration.SecretManager.Has(info.EnvVar)
                    ? (Router?.RegisteredProviders.Contains(name) == true ? "[green]✅ 就绪[/]" : "[yellow]🔑 已设(需重启)[/]")
                    : "[dim]未设置[/]";
            var isActive = string.Equals(name, prov, StringComparison.OrdinalIgnoreCase);
            sb.AppendLine($"  {(isActive ? "[cyan]> [/]" : "  ")}{name,-20} {keyStatus}");
        }
        return (sb.ToString(), true);
    }

    private static (string, bool) ConfigSelectProvider()
    {
        var prompt = new SelectionPrompt<string>()
            .Title("[yellow]选择 LLM Provider:[/]")
            .PageSize(15)
            .MoreChoicesText("[grey](滚动查看更多)[/]")
            .AddChoices(KnownProviders.Keys.OrderBy(k => k));
        var choice = AnsiConsole.Prompt(prompt);

        ActiveProvider = choice;
        var info = KnownProviders[choice];
        L1Model = info.Model;
        L2Model = info.Model;

        // Update router active provider
        if (Router != null) Router.ActiveProvider = choice;

        // Try to register if API key is available
        if (!string.IsNullOrEmpty(info.EnvVar))
        {
            var key = LTAI.Core.Configuration.SecretManager.Get(info.EnvVar);
            if (!string.IsNullOrEmpty(key) && Router != null && !Router.RegisteredProviders.Contains(choice))
            {
                var client = LTAI.AI.OpenAIChatClientFactory.Create(info.Endpoint, info.Model, key);
                Router.Register(choice, client);
            }
        }

        if (!string.IsNullOrEmpty(info.EnvVar) && !LTAI.Core.Configuration.SecretManager.Has(info.EnvVar))
            return ($"已切换到 [cyan]{choice}[/]。使用 /config apikey 设置 API Key", true);

        return ($"已切换到 [cyan]{choice}[/]", true);
    }

    private static (string, bool) ConfigSetApiKey(string providerArg)
    {
        var providerName = !string.IsNullOrEmpty(providerArg) ? providerArg : ActiveProvider;
        if (string.IsNullOrEmpty(providerName) || !KnownProviders.TryGetValue(providerName, out var info))
        {
            if (string.IsNullOrEmpty(providerArg))
                return ("用法: /config apikey <provider名称>  或先通过 /config provider 选择", true);
            return ($"未知 Provider '{providerArg}'。可用: {string.Join(", ", KnownProviders.Keys)}", true);
        }

        if (string.IsNullOrEmpty(info.EnvVar))
            return ($"{providerName} 为本地 Provider，无需 API Key", true);

        var key = AnsiConsole.Prompt(
            new TextPrompt<string>($"[yellow]输入 {providerName} API Key ({info.EnvVar}):[/]").Secret());
        if (string.IsNullOrWhiteSpace(key)) return ("已取消", true);

        LTAI.Core.Configuration.SecretManager.Set(info.EnvVar, key);

        // Register with router immediately
        var client = LTAI.AI.OpenAIChatClientFactory.Create(info.Endpoint, info.Model, key);
        Router?.Register(providerName, client);

        ActiveProvider ??= providerName;
        return ($"✅ [green]{info.EnvVar}[/] 已设置并注册", true);
    }

    private static (string, bool) ConfigSelectModel(string layer)
    {
        var info = ActiveProvider != null && KnownProviders.TryGetValue(ActiveProvider, out var p) ? p : null;
        if (info == null) return ("请先通过 /config provider 选择 Provider", true);

        if (!string.IsNullOrEmpty(info.EnvVar) && !LTAI.Core.Configuration.SecretManager.Has(info.EnvVar))
            return ("请先通过 /config apikey 设置 API Key", true);

        // Determine which layers to configure
        var configureL1 = string.IsNullOrEmpty(layer) || layer == "l1";
        var configureL2 = string.IsNullOrEmpty(layer) || layer == "l2";
        if (!configureL1 && !configureL2) configureL1 = configureL2 = true;

        // Fetch models from API
        List<string> models;
        if (!string.IsNullOrEmpty(info.EnvVar))
        {
            var apiKey = LTAI.Core.Configuration.SecretManager.Get(info.EnvVar);
            if (!string.IsNullOrEmpty(apiKey))
                models = FetchModelsFromApi(info.Endpoint, apiKey);
            else
                models = [];
        }
        else
        {
            models = [info.Model];
        }

        if (models.Count == 0)
        {
            // Fallback: show common models
            models =
            [
                info.Model,
                "gpt-4o", "gpt-4o-mini", "claude-3-opus", "claude-3-sonnet",
                "deepseek-v4-flash", "deepseek-reasoner", "qwen-plus", "qwen-turbo",
            ];
            AnsiConsole.MarkupLine("[yellow]无法从 API 获取模型列表，使用常用模型作为参考[/]");
        }

        if (configureL1)
        {
            var l1Prompt = new SelectionPrompt<string>()
                .Title("[yellow]选择 L1 (Fast) 模型:[/]")
                .PageSize(10)
                .MoreChoicesText("[grey](滚动查看更多)[/]")
                .AddChoices(models);
            var l1 = AnsiConsole.Prompt(l1Prompt);
            if (!string.IsNullOrEmpty(l1)) L1Model = l1;
        }

        if (configureL2)
        {
            var l2Prompt = new SelectionPrompt<string>()
                .Title("[yellow]选择 L2 (Pro) 模型:[/]")
                .PageSize(10)
                .MoreChoicesText("[grey](滚动查看更多)[/]")
                .AddChoices(models);
            var l2 = AnsiConsole.Prompt(l2Prompt);
            if (!string.IsNullOrEmpty(l2)) L2Model = l2;
        }

        return ($"L1: [cyan]{L1Model}[/]  L2: [cyan]{L2Model}[/]", true);
    }

    private static List<string> FetchModelsFromApi(string endpoint, string apiKey)
    {
        try
        {
            var http = _sharedHttp;
            var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = http.Send(req);
            if (!resp.IsSuccessStatusCode) return [];

            using var json = JsonDocument.Parse(resp.Content.ReadAsStream());
            return json.RootElement.GetProperty("data")
                .EnumerateArray()
                .Select(m => m.GetProperty("id").GetString() ?? "")
                .Where(id => !string.IsNullOrEmpty(id))
                .OrderBy(id => id)
                .ToList();
        }
        catch { return []; }
    }

    // ═══════════════════════════════════════════
    //  /config export / import
    // ═══════════════════════════════════════════

    private static (string, bool) ConfigExport(string fileArg)
    {
        var knownEnvVars = LTAI.Core.Configuration.KnownKeys.All
            .Select(k => k.EnvVar)
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct()
            .ToList();

        var config = new Dictionary<string, string>();
        foreach (var envVar in knownEnvVars)
        {
            var val = LTAI.Core.Configuration.SecretManager.Get(envVar);
            if (!string.IsNullOrEmpty(val))
                config[envVar] = val;
        }

        if (config.Count == 0)
            return ("没有已配置的环境变量可导出", true);

        var filePath = fileArg;
        if (string.IsNullOrEmpty(filePath))
        {
            var defaultName = $"ltai-config-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            filePath = Path.Combine(Directory.GetCurrentDirectory(), defaultName);
        }

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);

        var absPath = Path.GetFullPath(filePath);
        return ($"✅ 已导出 {config.Count} 个环境变量到 [cyan]{absPath}[/]\n" +
                "[yellow]⚠ 此文件包含 API Key，请妥善保管，不要提交到版本控制[/]", true);
    }

    private static (string, bool) ConfigImport(string fileArg)
    {
        if (string.IsNullOrWhiteSpace(fileArg))
            return ("用法: /config import <文件路径>", true);

        var filePath = fileArg;
        if (!File.Exists(filePath))
        {
            // Try relative to CWD
            filePath = Path.Combine(Directory.GetCurrentDirectory(), fileArg);
            if (!File.Exists(filePath))
                return ($"文件不存在: {fileArg}", true);
        }

        Dictionary<string, string>? config;
        try
        {
            var text = File.ReadAllText(filePath);
            config = JsonSerializer.Deserialize<Dictionary<string, string>>(text);
        }
        catch (Exception ex)
        {
            return ($"解析配置文件失败: {ex.Message}", true);
        }

        if (config == null || config.Count == 0)
            return ("配置文件中没有有效的环境变量", true);

        var imported = 0;
        foreach (var (envVar, value) in config)
        {
            if (!string.IsNullOrEmpty(envVar) && !string.IsNullOrEmpty(value))
            {
                LTAI.Core.Configuration.SecretManager.Set(envVar, value);

                // Try to register with router if it matches a known provider
                var providerName = KnownProviders
                    .Where(kv => string.Equals(kv.Value.EnvVar, envVar, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Key)
                    .FirstOrDefault();

                if (providerName != null && Router != null)
                {
                    var info = KnownProviders[providerName];
                    var client = LTAI.AI.OpenAIChatClientFactory.Create(info.Endpoint, info.Model, value);
                    Router.Register(providerName, client);
                }
                imported++;
            }
        }

        return ($"✅ 已导入 {imported}/{config.Count} 个环境变量\n" +
                "[yellow]部分 Provider 已自动注册，使用 /config status 查看状态[/]", true);
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

    // ═══════════════════════════════════════════
    //  /pipe commands — P16.1: list/run sequential/concurrent pipelines
    // ═══════════════════════════════════════════

    // ── B1+B2: TUI /lang command ──
    private static (string, bool) HandleLangCommand(string args)
    {
        var lang = args.Trim().ToLowerInvariant();
        if (lang == "zh-cn" || lang == "zh" || lang == "cn")
        {
            LTAI.Core.I18n.Locale.SetLang("zh-CN");
            return ("已切换界面语言: 中文", true);
        }
        if (lang == "en-us" || lang == "en" || lang == "us")
        {
            LTAI.Core.I18n.Locale.SetLang("en-US");
            return ("Language switched: English", true);
        }
        return ($"Usage: /lang zh-CN|en-US (current: {LTAI.Core.I18n.Locale.CurrentLang})", true);
    }

    private static (string, bool) HandlePipeCommand(string args)
    {
        var pipes = Pipes;
        var registry = WorkflowRegistry;
        if (pipes == null)
            return ("Pipes (AgentWorkflows) not initialized", true);

        var parts = args.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var subArgs1 = parts.Length > 1 ? parts[1].Trim() : "";
        var subArgs2 = parts.Length > 2 ? parts[2].Trim() : "";

        return sub switch
        {
            "" or "list" => PipelinesList(registry),
            "run" => PipeRun(pipes, registry, subArgs1, subArgs2),
            "stop" => ("Run cancellation via tools, not /pipe stop", true),
            _ => ("用法: /pipe list | run <preset> [task] | stop <id>", true),
        };
    }

    private static (string, bool) PipelinesList(LTAI.Agent.Workflows.YAMLWorkflowRegistry? registry)
    {
        if (registry == null)
            return ("[yellow]暂无 pipeline 配置[/]  请创建 sequential/concurrent JSON 文件", true);

        var info = registry.List();
        var pipelinePresets = info.Where(w => w.Type is "sequential" or "concurrent").ToList();
        if (pipelinePresets.Count == 0)
            return ("[yellow]暂无 pipeline 配置[/]  创建 sequential.json / concurrent.json 后重试", true);

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Preset");
        table.AddColumn("Type");
        table.AddColumn("V");
        table.AddColumn("Agents/Sources");
        table.AddColumn("Path");

        foreach (var p in pipelinePresets)
        {
            var cfg = registry.TryGetPipelineConfig(p.Name);
            var agents = cfg?.Agents ?? [];
            var agentsStr = agents.Count > 0
                ? string.Join(", ", agents.Select(a => $"[cyan]{a}[/]"))
                : "[grey](empty)[/]";
            var fileName = System.IO.Path.GetFileName(p.FilePath);
            table.AddRow(
                $"[cyan]{p.Name.EscapeMarkup()}[/]",
                $"[grey]{p.Type.EscapeMarkup()}[/]",
                p.Version.ToString(),
                agentsStr,
                $"[grey]{fileName.EscapeMarkup()}[/]");
        }

        AnsiConsole.Write(table);
        return ($"[grey]共 {pipelinePresets.Count} 个 pipeline · /pipe run <name> [task] 执行[/]", true);
    }

    private static (string, bool) PipeRun(
        LTAI.Agent.Workflows.AgentWorkflows pipes,
        LTAI.Agent.Workflows.YAMLWorkflowRegistry? registry,
        string presetName,
        string task)
    {
        if (string.IsNullOrEmpty(presetName))
            return ("用法: /pipe run <preset> [task]  — 例如: /pipe run sequential \"写一篇博客\"", true);

        if (registry == null)
            return ("Workflow registry not available; cannot resolve pipeline preset", true);

        var cfg = registry.TryGetPipelineConfig(presetName);
        if (cfg == null)
        {
            var info = registry.List();
            var pipeNames = info.Where(w => w.Type is "sequential" or "concurrent").Select(w => w.Name).ToList();
            var hint = pipeNames.Count > 0
                ? $"可用: {string.Join(", ", pipeNames)}"
                : "没有可用 pipeline。创建 sequential.json / concurrent.json 后重试";
            return ($"未知 pipeline '[red]{presetName}[/]'  {hint}", true);
        }

        var defaultTask = string.IsNullOrEmpty(task)
            ? cfg.DefaultTask ?? "请根据预设 agents 列表完成任务"
            : task;

        if (cfg.Type == "concurrent")
        {
            AnsiConsole.MarkupLine($"[yellow]⏳[/] 并发 pipeline [cyan]{presetName}[/] on: [grey]{defaultTask.EscapeMarkup()}[/]");
            var result = Task.Run(() => pipes.RunConcurrentAsync([presetName], defaultTask, ct: default)).GetAwaiter().GetResult();
            AnsiConsole.MarkupLine(result);
            return ($"[green]✅ 并发完成[/] 请查看上方结果", true);
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]⏳[/] 顺序 pipeline [cyan]{presetName}[/] on: [grey]{defaultTask.EscapeMarkup()}[/]");
            var result = Task.Run(() => pipes.RunSequentialAsync([presetName], defaultTask, ct: default)).GetAwaiter().GetResult();
            AnsiConsole.MarkupLine(result);
            return ($"[green]✅ 顺序完成[/] 请查看上方结果", true);
        }
    }

    // ═══════════════════════════════════════════
    //  /jobs commands — P14.14: list/watch/cancel/show
    // ═══════════════════════════════════════════

    private static (string, bool) HandleJobsCommand(string args)
    {
        var jobs = Jobs;
        if (jobs == null)
            return ("Background job service not initialized (BGJS missing in DI)", true);

        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var subArgs = parts.Length > 1 ? parts[1].Trim() : "";

        return sub switch
        {
            "" or "list" => JobsList(jobs),
            "watch" => JobsWatch(jobs, subArgs),
            "cancel" => JobsCancel(jobs, subArgs),
            "show" => JobsShow(jobs, subArgs),
            _ => ("用法: /jobs list | watch <id> | cancel <id> | show <id>", true),
        };
    }

    private static (string, bool) JobsList(LTAI.Agent.Tools.BackgroundJobService jobs)
    {
        var snap = jobs.SnapshotJobs();
        if (snap.Count == 0)
            return ("[yellow]暂无后台作业[/]  用法: 让 agent 跑 `start_job` 创建", true);

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
        return ($"[grey]共 {snap.Count} 个作业 (60s 后自动清理)[/]", true);
    }

    private static (string, bool) JobsWatch(LTAI.Agent.Tools.BackgroundJobService jobs, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return ("用法: /jobs watch <id>  例如: /jobs watch 3", true);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timeout = TimeSpan.FromMinutes(2);
        LTAI.Agent.Tools.JobEntry? lastEntry = null;

        AnsiConsole.MarkupLine($"[grey]Watching job #{id}... (Ctrl+C 退出, 最多 2 分钟)[/]");
        while (sw.Elapsed < timeout)
        {
            var entry = jobs.GetJobEntry(id);
            if (entry == null)
                return ($"[yellow]⚠ Job #{id} 已被清理（60s 过期或不存在）[/]", true);

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

        return ($"[yellow]⏱ 2 分钟超时，job #{id} 仍在运行。退出 watch（job 仍存在）[/]", true);
    }

    private static (string, bool) JobsCancel(LTAI.Agent.Tools.BackgroundJobService jobs, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return ("用法: /jobs cancel <id>", true);

        var entry = jobs.GetJobEntry(id);
        if (entry == null)
            return ($"[yellow]⚠ Job #{id} 不存在（可能已完成并被清理）[/]", true);

        if (entry.Completed)
            return ($"[grey]Job #{id} 已完成 (exit={entry.ExitCode}), 无需取消[/]", true);

        entry.Completed = true;
        entry.Error = "Cancelled";
        return ($"[green]✅ 已标记取消[/] Job #{id} (BGJS 不杀进程, residual 退出后自然消失)", true);
    }

    private static (string, bool) JobsShow(LTAI.Agent.Tools.BackgroundJobService jobs, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return ("用法: /jobs show <id>", true);

        var entry = jobs.GetJobEntry(id);
        if (entry == null)
            return ($"[yellow]⚠ Job #{id} 不存在（可能已完成并被清理）[/]", true);

        var elapsed = DateTime.UtcNow - entry.StartedAtUtc;
        var sb = new System.Text.StringBuilder();
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
            Directory.SetCurrentDirectory(newDir);
            return ($"已切换到: {newDir}", true);
        }
        catch (Exception ex) { return ($"切换失败: {ex.Message}", true); }
    }

    /// <summary>列出目录内容，用 Spectre.Console Tree 展示。</summary>
    private static (string, bool) ListDir(string path)
    {
        try
        {
            var dir = !string.IsNullOrWhiteSpace(path)
                ? Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path))
                : Directory.GetCurrentDirectory();

            if (!Directory.Exists(dir))
                return ($"[red]目录不存在:[/] {dir}", true);

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

    /// <summary>Set the sandbox root (called by TuiApp on startup).</summary>
    [Obsolete("Sandbox restriction removed — /cd now allows any path.")]
    public static void SetRootPath(string root) { }

    private static int Levenshtein(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    private sealed record SlashSpec(string Cmd, string Group, string Summary,
        string Aliases = "", string? ArgsHint = null, bool Info = false);
}
