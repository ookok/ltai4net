using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using LTAI.AI;

namespace LTAI.Agent.Tools;

[ToolDomain("subagent")]
public sealed class SubagentTools
{
    private readonly IServiceProvider _sp;
    private readonly IChatClient _llm;
    private readonly string _ws;
    private readonly IReadOnlyList<AITool> _allTools;

    private int _spawnCount;
    private int _totalTurns;
    private readonly object _budgetLock = new();

    /// <summary>子 Agent 消息流式回调：spawnCount, role, content</summary>
    public static event Action<int, string, string>? OnSubagentMessage;
    /// <summary>子 Agent 完成回调：spawnCount</summary>
    public static event Action<int>? OnSubagentComplete;

    private const int MaxSpawns = 10;
    private const int TotalTurnLimit = 50;

    // Read-only tool name prefixes (for explore/review/security_review)
    private static readonly HashSet<string> ReadOnlyPrefixes =
    [
        "Read", "Search", "Glob", "List", "Get",
        "DirectoryTree", "Fetch", "Find", "Lookup",
        "Ping", "Dns", "Check", "Whois", "HttpCheck",
        "Network", "SystemInfo", "ListProcesses", "GetEnv",
    ];

    // Tools explicitly denied for ALL subagents (prevent recursion + dangerous ops)
    private static readonly HashSet<string> DeniedTools =
    [
        "SpawnSubagent", "spawn_subagent",
        "WriteFile", "EditFile", "MultiEdit", "DeleteFile",
        "MoveFile", "CopyFile",
        "GitCommit", "GitPush", "GitCommitAndPush",
        "RunCommand", "SafeShell",
        "RunInContainer", "RunWithNetwork",
    ];

    private static readonly string SubagentBaseSystem = """
        You are an LTAI subagent. The parent agent spawned you for one focused subtask.

        Rules:
        - Stay on task. Do not expand scope.
        - Your final message is all the parent sees. Make it complete and self-contained.
        - No follow-up offers, no "let me know if you need more."
        - Do NOT call spawn_subagent (you are already a subagent — that would create a recursive loop).
        """;

    private static readonly HashSet<string> ReadOnlyTypes = ["explore", "review", "security_review"];

    public SubagentTools(IServiceProvider sp, IChatClient llm, string ws, IReadOnlyList<AITool>? allTools = null)
    {
        _sp = sp;
        _llm = llm;
        _ws = ws;
        _allTools = allTools ?? [];
    }

    [Description("在独立的子 Agent 中探索代码库：只读式广域网调查，返回精炼结论。\n"
        + "适用场景：跨多个文件调查代码结构、搜索项目中某个功能的所有实现位置。\n"
        + "不适用场景：需要修改代码（只读）、需要结合网络搜索（请用 Research）。\n"
        + "关键参数：task — 具体的调查问题。")]
    [ToolExample("调查这个项目中哪些地方用到了 HttpClient")]
    public Task<string> Explore(
        [Description("Concrete investigation question")] string task,
        string? traceId = null,
        CancellationToken ct = default)
        => SpawnAsync(task, "explore", traceId: traceId, ct: ct);

    [Description("结合代码阅读与网络搜索在子 Agent 中进行调研，返回综合分析结果。\n"
        + "适用场景：需要查资料才能回答的问题、技术选型调研、了解某个库的用法。\n"
        + "不适用场景：只需代码调查（请用 Explore）、只需代码审查（请用 Review）。\n"
        + "关键参数：task — 需要调研的问题描述。")]
    [ToolExample("调研一下 .NET 10 的新特性")]
    public Task<string> Research(
        [Description("Research question requiring web + code")] string task,
        string? traceId = null,
        CancellationToken ct = default)
        => SpawnAsync(task, "research", traceId: traceId, ct: ct);

    [Description("在子 Agent 中审查代码变更：标记正确性、安全性、缺失测试、隐藏行为变更。\n"
        + "适用场景：提交代码前审查 diff、检查 PR 的潜在问题、代码质量审计。\n"
        + "不适用场景：安全专项审查（请用 SecurityReview）。\n"
        + "关键参数：task — 审查重点或 'general'。")]
    [ToolExample("审查我当前的代码变更")]
    public Task<string> Review(
        [Description("Focus area or 'general'")] string task,
        string? traceId = null,
        CancellationToken ct = default)
        => SpawnAsync(task, "review", traceId: traceId, ct: ct);

    [Description("在子 Agent 中进行安全专项审查：注入/认证/密钥/反序列化/路径穿越/加密问题。\n"
        + "适用场景：需要出安全的代码变更提交前审查、发现潜在安全漏洞。\n"
        + "不适用场景：常规代码审查（请用 Review）。\n"
        + "关键参数：task — 审查范围或 'full'。")]
    [ToolExample("安全审查这次提交的变更")]
    public Task<string> SecurityReview(
        [Description("Scope hint or 'full'")] string task,
        string? traceId = null,
        CancellationToken ct = default)
        => SpawnAsync(task, "security_review", traceId: traceId, ct: ct);

    [Description("启动一个隔离的子 Agent 执行独立任务。每次生成会支付 prefix-cache miss + 完整子循环——优先使用直接工具。\n"
        + "适用场景：需要并行执行独立调查、任务需要隔离环境、长文本分析。\n"
        + "不适用场景：简单的单步操作（请用直接工具更高效）。\n"
        + "关键参数：task — 子 Agent 要执行的任务描述。")]
    [ToolExample("另起一个子任务来分析这个日志文件")]
    public Task<string> SpawnSubagent(
        [Description("The subtask to perform")] string task,
        [Description("Optional type: explore, research, review, security_review")] string? type = null,
        [Description("Optional system prompt override")] string? system = null,
        string? traceId = null,
        CancellationToken ct = default)
        => SpawnAsync(task, type, system, traceId, ct);

    private async Task<string> SpawnAsync(string task, string? type, string? systemOverride = null,
        string? traceId = null, CancellationToken ct = default)
    {
        string? budgetHint;
        lock (_budgetLock)
        {
            _spawnCount++;
            _totalTurns++;
            budgetHint = GetBudgetHint();
            if (_spawnCount > MaxSpawns)
                return ToolResult.Error($"Budget exceeded: {_spawnCount} spawns (max {MaxSpawns}). " +
                    "Use direct tools instead of spawning more subagents.");
        }

        if (!string.IsNullOrEmpty(traceId))
            System.Diagnostics.Debug.WriteLine($"[Subagent {_spawnCount}] type={type} trace={traceId}");

        var systemPrompt = systemOverride ?? GetSystemPrompt(type ?? "generic");
        var isReadOnly = ReadOnlyTypes.Contains(type ?? "");

        if (isReadOnly)
            systemPrompt += "\n\nIMPORTANT: You are in read-only mode. Use only read_file, search_content, directory_tree, list_files, glob. Do NOT write/edit/delete any files.";

        var subTools = FilterTools(isReadOnly);
        var chatOptions = new ChatOptions
        {
            Temperature = 0.3f,
            MaxOutputTokens = 4096,
            Tools = subTools.Count > 0 ? subTools : null,
        };

        try
        {
            var capturedSpawn = _spawnCount;
            var capturedType = type ?? "generic";

            var agent = new ChatClientAgent(_llm, new ChatClientAgentOptions
            {
                Name = $"subagent-{capturedSpawn}",
                Description = systemPrompt,
                ChatOptions = chatOptions,
                ChatHistoryProvider = new InMemoryChatHistoryProvider(),
            }, null, _sp);

            var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            var messageBuf = new StringBuilder();
            var messages = new List<(string role, string content)>();

            // 触发用户消息事件
            OnSubagentMessage?.Invoke(capturedSpawn, "user", task);

            // 使用流式执行，每 token 触发 UI 事件
            await foreach (var update in agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, task)], session, cancellationToken: ct))
            {
                // 收集文本 token
                if (!string.IsNullOrEmpty(update.Text))
                {
                    messageBuf.Append(update.Text);
                }

                // 处理工具调用/结果
                if (update.Contents?.Count > 0)
                {
                    foreach (var c in update.Contents)
                    {
                        if (c is FunctionCallContent fc)
                        {
                            var msg = $"🛠 调用 {fc.Name}";
                            OnSubagentMessage?.Invoke(capturedSpawn, "assistant", msg);
                        }
                        if (c is FunctionResultContent frc)
                        {
                            var resultStr = frc.Result?.ToString() ?? "";
                            var preview = resultStr.Length > 80 ? resultStr[..80] + "..." : resultStr;
                            OnSubagentMessage?.Invoke(capturedSpawn, "assistant", $"  📄 {preview}");
                        }
                    }
                }

                // 每收到完整句子或工具调用就触发事件
                if (update.Contents?.Count > 0 || (update.Text?.Contains('\n') == true))
                {
                    var text = messageBuf.ToString().Trim();
                    if (text.Length > 0)
                    {
                        OnSubagentMessage?.Invoke(capturedSpawn, "assistant", text);
                        messages.Add(("assistant", text));
                        messageBuf.Clear();
                    }
                }
            }

            // 最后的文本
            var finalText = messageBuf.ToString().Trim();
            if (finalText.Length > 0)
            {
                OnSubagentMessage?.Invoke(capturedSpawn, "assistant", finalText);
                messages.Add(("assistant", finalText));
            }

            var elapsed = sw.ElapsedMilliseconds;
            var resultText = messages.Count > 0 ? messages[^1].Item2 : "(no output)";

            var output = JsonSerializer.Serialize(new
            {
                success = true,
                output = Truncate(resultText, 8000),
                spawnCount = capturedSpawn,
                elapsedMs = elapsed,
                type = capturedType,
            });

            OnSubagentComplete?.Invoke(capturedSpawn);
            return budgetHint != null ? $"{output}\n{budgetHint}" : output;
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error("Subagent cancelled by user");
        }
        catch (Exception ex)
        {
            return ToolResult.FromException(ex, "Subagent failed");
        }
    }

    private List<AITool> FilterTools(bool readOnly)
    {
        return _allTools.Where(t =>
        {
            var name = t.Name ?? "";
            if (DeniedTools.Any(d => name.Equals(d, StringComparison.OrdinalIgnoreCase)))
                return false;
            if (readOnly && !ReadOnlyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                return false;
            return true;
        }).ToList();
    }

    private string? GetBudgetHint()
    {
        if (_spawnCount > 5 || _totalTurns > 30)
            return $"[budget: this session has spawned {_spawnCount} subagents ({_totalTurns} turns). " +
                   "Confirm the next spawn is genuinely needed before calling spawn_subagent again.]";
        if (_spawnCount > 2)
            return $"[note: {_spawnCount} subagents spawned this session; confirm this one is worth it.]";
        return null;
    }

    private static string GetSystemPrompt(string type) => type switch
    {
        "explore" => $"{SubagentBaseSystem}\n\nYou are an exploration subagent. Use read_file, search_content, directory_tree. Return a distilled answer with file:line citations.",
        "research" => $"{SubagentBaseSystem}\n\nYou are a research subagent. Use web_search for external references, read_file/search_content for local verification. Synthesize findings.",
        "review" => $"{SubagentBaseSystem}\n\nYou are a code review subagent. Check correctness, security, missing tests. Return severity-tagged findings with file:line.",
        "security_review" => $"{SubagentBaseSystem}\n\nYou are a security review subagent. Check injection, auth, secrets, deserialization, path traversal. Return severity-tagged list with CWE references.",
        _ => SubagentBaseSystem
    };

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + $"\n... (truncated, {text.Length - max} more chars)";
}
