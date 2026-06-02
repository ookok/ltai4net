using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;

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

    /// <summary>Current L1 (fast) model name.</summary>
    public static string? L1Model { get; set; }
    /// <summary>Current L2 (pro) model name.</summary>
    public static string? L2Model { get; set; }
    /// <summary>Current active provider name.</summary>
    public static string? ActiveProvider { get; set; }

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
        new("config",  "设置",  "配置 LLM: provider|apikey|model|status", "", "provider|apikey|model|l1|l2"),
        new("model",   "设置",  "管理 ONNX 向量模型: list|download|delete|switch", "", "list|download <id>|delete <id>|switch <id>"),
        new("status",  "信息",  "显示当前配置和统计", "状态,统计"),
        new("monitor", "信息",  "实时仪表盘 — Provider 状态/延迟/成本", "监控,仪表盘"),
        new("cost",    "信息",  "显示本轮预估费用", "费用,花费"),
        new("memory",  "扩展",  "管理记忆文件", "记忆"),
        new("skill",   "扩展",  "列出/运行技能", "", "技能名"),
        new("mode",    "代码",  "编辑模式: review|auto", "", "review|auto"),
        new("undo",    "代码",  "撤销上次编辑", "撤销"),
        new("ls",      "文件",  "列出当前目录内容", "dir,列表"),
        new("cd",      "文件",  "切换工作目录", "", "目录路径"),
        new("pwd",     "文件",  "显示当前目录", "目录"),
        new("approve", "计划",  "批准当前计划并开始执行", "yes,confirm,批准,确认"),
        new("plan",    "计划",  "查看当前计划状态", "计划状态"),
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
                        $"[/]{alias}  [dim]{spec.Group} → /{spec.Cmd}[/]",
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
                    $"[/]{alias}  [dim]{spec.Group} → /{spec.Cmd}[/]",
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
            var spec = Commands.First(c => c.Cmd == cmdName);

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
        var (h, s) = spec.Cmd switch
        {
            "help" => Help(),
            "exit" => ("", false),
            "new" => ("Session cleared. Starting fresh.", true),
            "retry" => ("Retrying last message...", true),
            "compact" => ("Summarizing older turns...", true),
            "model" => HandleModelCommand(args),
            "config" => HandleConfigCommand(args),
            "status" => Status(),
            "monitor" => Monitor(),
            "cost" => ("Cost tracking: see model provider dashboard", true),
            "memory" => ("Memory: use `remember` / `forget` tools", true),
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

    /// <summary>Handle /model list|download|delete|switch subcommands.</summary>
    private static (string, bool) HandleModelCommand(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var subCmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var subArgs = parts.Length > 1 ? parts[1] : "";

        var embedder = Embedder;
        if (embedder == null)
            return ("ONNX embedder not available", true);

        return subCmd switch
        {
            "list" => HandleModelList(embedder),
            "switch" => HandleModelSwitch(embedder, subArgs),
            "download" => HandleModelDownload(embedder, subArgs),
            "delete" => HandleModelDelete(embedder, subArgs),
            _ => ("用法: /model list|download <id>|delete <id>|switch <id>", true),
        };
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

        return ($"已切换到 ONNX 模型: [green]{name}[/]（{LTAI.AI.LocalEmbedder.KnownModels[name].DisplayName}）", true);
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
                "deepseek-chat", "deepseek-reasoner", "qwen-plus", "qwen-turbo",
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
