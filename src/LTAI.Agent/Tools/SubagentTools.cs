using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using LTAI.AI;
using LTAI.Agent.Prompts;
using LTAI.Core.Configuration;

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

    /// <summary>子 Agent 消息流式回调：spawnCount, role, content.
    /// Static for global hook compatibility; use <see cref="Message"/> for per-instance events.</summary>
    public static event Action<int, string, string>? OnSubagentMessage;
    /// <summary>子 Agent 完成回调：spawnCount.
    /// Static for global hook compatibility; use <see cref="Completed"/> for per-instance events.</summary>
    public static event Action<int>? OnSubagentComplete;

    /// <summary>Per-instance message event (avoids cross-instance handler leaks).</summary>
    public event Action<int, string, string>? Message;
    /// <summary>Per-instance completion event.</summary>
    public event Action<int>? Completed;

    private const int MaxSpawns = 10;
    private const int TotalTurnLimit = 50;

    // Read-only tools are identified by [ReadOnlyTool] attribute on their method.
    // The old prefix-based approach (Read/Get/List etc.) was fragile —
    // a method named "ReadWriteSettings" would be incorrectly let through.
    // Attribute-based matching is exact and explicit.

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

    private static readonly Lazy<string> _subagentBaseSystem = new(() =>
    {
        var filePrompt = PromptLoader.Load("subagent-base");
        if (!string.IsNullOrEmpty(filePrompt)) return filePrompt;
        return """
    You are an LTAI subagent. The parent agent spawned you for one focused subtask.

    Rules:
    - Stay on task. Do not expand scope.
    - Your final message is all the parent sees. Make it complete and self-contained.
    - No follow-up offers, no "let me know if you need more."
    - Do NOT call spawn_subagent (you are already a subagent — that would create a recursive loop).
    """;
    });

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

    /// <summary>
    /// Deep research orchestrator: scout → decompose → parallel lane research → verification → report.
    /// Inspired by architect-loop's /architect-research: scout-first, topic-specific lane design,
    /// parallel gathering, single-author report with source discipline.
    /// </summary>
    [Description("深度调研编排器：侦察→专题分解→并行调研→验证→报告撰写。\n"
        + "适用场景：撰写行业报告、技术选型调研、SOTA 综述、竞争对手分析。\n"
        + "参数: topic — 调研主题; depth — brief(快速)/standard(标准)/deep(深度); "
        + "saveToDisk — 是否保存为 docs/reports/<topic>.md")]
    [ToolExample("调研 Rust 在嵌入式领域的应用现状")]
    public async Task<string> DeepResearch(
        [Description("Research topic/question")] string topic,
        [Description("Depth: brief (2 lanes), standard (4 lanes, default), deep (6 lanes)")] string depth = "standard",
        [Description("Save report to disk as docs/reports/<slug>.md")] bool saveToDisk = true,
        CancellationToken ct = default)
    {
        var laneCount = depth.ToLowerInvariant() switch
        {
            "brief" => 2,
            "deep" => 6,
            _ => 4,
        };
        var searchesPerLane = depth.ToLowerInvariant() switch
        {
            "brief" => 8,
            "deep" => 20,
            _ => 12,
        };

        var sw = Stopwatch.StartNew();
        var report = new StringBuilder();
        report.AppendLine($"# Deep Research: {topic}\n");
        report.AppendLine($"> Depth: {depth} | Lanes: {laneCount} | Searches/lane: {searchesPerLane}\n");

        // Phase 1: Scout — map the topic landscape
        report.AppendLine("## Phase 1: Scout\n");
        var scoutResult = await ScoutInternal(topic, ct).ConfigureAwait(false);
        report.AppendLine(scoutResult);
        report.AppendLine();

        if (ct.IsCancellationRequested) return report.ToString();

        // Phase 2: Decompose — design research lanes from scout map
        report.AppendLine("## Phase 2: Lane Design\n");
        var lanes = await DecomposeInternal(topic, scoutResult, laneCount, ct).ConfigureAwait(false);
        report.AppendLine(string.Join("\n", lanes.Select((l, i) => $"  Lane {i + 1}: {l}")));
        report.AppendLine();

        if (ct.IsCancellationRequested || lanes.Length == 0) return report.ToString();

        // Phase 3: Fan-out — parallel lane research
        report.AppendLine($"## Phase 3: Research ({lanes.Length} lanes, {searchesPerLane} searches/lane)\n");
        var laneResults = new (string lane, string result)[lanes.Length];
        var semaphore = new SemaphoreSlim(Math.Min(4, lanes.Length), Math.Min(4, lanes.Length));
        var laneTasks = lanes.Select(async (lane, i) =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                laneResults[i] = (lane, await ResearchLaneInternal(lane, topic, searchesPerLane, ct).ConfigureAwait(false));
            }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(laneTasks).ConfigureAwait(false);
        foreach (var (lane, result) in laneResults)
            report.AppendLine($"### Lane: {lane}\n\n{TruncateForReport(result, 1500)}\n");
        report.AppendLine();

        if (ct.IsCancellationRequested) return report.ToString();

        // Phase 4: Verification — check load-bearing claims
        report.AppendLine("## Phase 4: Verification\n");
        var verifyResult = await VerifyInternal(topic, laneResults.Select(r => r.result).ToList(), ct).ConfigureAwait(false);
        report.AppendLine(verifyResult);
        report.AppendLine();

        if (ct.IsCancellationRequested) return report.ToString();

        // Phase 5: Write final report
        report.AppendLine("## Phase 5: Final Report\n\n");
        var finalReport = await WriteReportInternal(topic, laneResults, verifyResult, ct).ConfigureAwait(false);
        report.AppendLine(finalReport);

        sw.Stop();

        // Save to disk (best-effort, preserve report even if write fails)
        var fullReport = $"# {topic}\n\n> Generated by LTAI DeepResearch | Depth: {depth} | Duration: {sw.Elapsed.TotalSeconds:F1}s | Lanes: {laneCount}\n\n{finalReport}";
        if (saveToDisk)
        {
            try
            {
                var slug = string.Join("-", topic.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Take(8).Select(s => new string(s.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray()).ToLowerInvariant()));
                if (string.IsNullOrEmpty(slug) || slug == new string('-', slug.Length))
                    slug = $"research-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
                var dir = Path.Combine(_ws, "docs", "reports");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"{slug}.md");
                await File.WriteAllTextAsync(path, fullReport, ct).ConfigureAwait(false);
                report.AppendLine($"\n---\nSaved to: {path}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"\n---\n⚠️ Save failed: {ex.Message}");
            }
        }

        return report.ToString();
    }

    // ═══════════════════════════════════════════════════
    //  Deep Research Internal Helpers
    // ═══════════════════════════════════════════════════

    private async Task<string> ScoutInternal(string topic, CancellationToken ct)
    {
        var systemPrompt = $"""
        {_subagentBaseSystem.Value}

        You are a research scout. Map the landscape of: "{topic}"

        Return a structured map with these sections (keep each section concise, max 5 bullets):
        ## Key Terminology
        ## Load-Bearing Systems / Frameworks / Papers
        ## Named People / Organizations
        ## Natural Fault Lines (disagreements, competing approaches, open questions)
        ## Suggested Research Angles (4-8 distinct angles for deep investigation)

        Output only the map, no preamble.
        """;

        var result = await SpawnInternalAsync(
            $"Map the research landscape for: {topic}. Identify terminology, key systems, named people, fault lines, and suggest research angles.",
            systemPrompt, readOnly: true, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<string[]> DecomposeInternal(string topic, string scoutResult, int count, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, $"""
            You are a research decomposition assistant. Given a topic and a landscape scout map,
            design {count} distinct, non-overlapping research lanes.

            Rules:
            - Each lane targets ONE specific aspect/angle/question
            - Lanes must be mutually exclusive (no overlap in what they investigate)
            - Lanes must be collectively exhaustive (together they cover the topic)
            - Output each lane as a single line: "LANE: <specific research question or angle>"
            - No preamble, no markdown, just the LANE: lines
            """),
            new(ChatRole.User, $"Topic: {topic}\n\nScout Map:\n{scoutResult}\n\nDesign {count} lanes:"),
        };

        var result = await _llm.GetResponseAsync(messages, new ChatOptions
        {
            Temperature = 0.5f, MaxOutputTokens = 1024,
        }, ct).ConfigureAwait(false);

        var text = result.Text ?? "";
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("LANE:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["LANE:".Length..].Trim())
            .Take(count)
            .ToArray();
    }

    private async Task<string> ResearchLaneInternal(string lane, string topic, int maxSearches, CancellationToken ct)
    {
        var systemPrompt = $"""
        {_subagentBaseSystem.Value}

        You are a focused research agent investigating one lane of a larger research project.

        Main topic: {topic}
        Your lane: {lane}

        Rules:
        - Max {maxSearches} web searches — stop when you have 3+ solid findings
        - Every finding MUST include: URL, date, exact quote or figure, confidence tag (HIGH/MEDIUM/LOW)
        - "NOT FOUND" beats inference — never fabricate
        - Report disagreements between sources, don't resolve them
        - NO recommendations — raw findings only
        - Output format:
          ## Findings for: {lane}
          ### Finding 1
          - Source: <URL> (date)
          - Quote: "<exact text>"
          - Confidence: HIGH/MEDIUM/LOW
          ### Finding 2 ...
        """;

        return await SpawnInternalAsync(
            $"Research lane: {lane}. Find concrete evidence, sources, and data. Max {maxSearches} searches.",
            systemPrompt, readOnly: true, ct).ConfigureAwait(false);
    }

    private async Task<string> VerifyInternal(string topic, List<string> laneResults, CancellationToken ct)
    {
        var allRaw = string.Join("\n\n---\n\n", laneResults);
        var systemPrompt = $"""
        {_subagentBaseSystem.Value}

        You are a research verifier. Check load-bearing claims from research lanes.

        Rules:
        - Flag claims with <2 independent sources as UNVERIFIED
        - Identify conflicting claims between lanes as DISPUTED
        - For critical claims, suggest an adversarial search query
        - Tag each significant claim: VERIFIED / UNVERIFIED / DISPUTED / SUSPICIOUS
        - Output summary: number of verified, unverified, disputed claims + top 3 verification gaps
        """;

        return await SpawnInternalAsync(
            $"Verify the research findings below. Check source independence, identify disputes, flag unverified claims.\n\n{TruncateForReport(allRaw, 8000)}",
            systemPrompt, readOnly: true, ct).ConfigureAwait(false);
    }

    private async Task<string> WriteReportInternal(string topic,
        (string lane, string result)[] laneResults, string verifyResult, CancellationToken ct)
    {
        var laneSummaries = string.Join("\n\n---\n\n", laneResults.Select(r =>
            $"## Lane: {r.lane}\n\n{TruncateForReport(r.result, 3000)}"));

        var systemPrompt = $"""
        {_subagentBaseSystem.Value}

        You are a research report writer. Synthesize the findings below into a coherent, decision-oriented report.

        Rules:
        - Single author writes the entire report — no section delegation
        - Answer first, then explain (inverted pyramid)
        - Every load-bearing claim needs ≥2 independent sources
        - Citation format: [Source](URL) — date
        - Sections: Summary → Key Findings → Evidence by Theme → Open Questions → Recommendations
        - Include the verification notes:
        {TruncateForReport(verifyResult, 2000)}
        - Target: professional, comprehensive, well-structured markdown
        """;

        return await SpawnInternalAsync(
            $"Write the final research report for: {topic}. Synthesize all lane findings into one cohesive document.\n\nLane Findings:\n{laneSummaries}",
            systemPrompt, readOnly: true, ct).ConfigureAwait(false);
    }

    private async Task<string> SpawnInternalAsync(string task, string systemPrompt,
        bool readOnly = true, CancellationToken ct = default)
    {
        var subTools = FilterTools(readOnly);
        var chatOptions = new ChatOptions
        {
            Temperature = 0.3f,
            MaxOutputTokens = 4096,
            Tools = subTools.Count > 0 ? subTools : null,
        };

        try
        {
            var agent = new ChatClientAgent(_llm, new ChatClientAgentOptions
            {
                Name = "deep-research",
                Description = systemPrompt,
                ChatOptions = chatOptions,
                ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
                {
                    ChatReducer = new MaxMessageCountReducer(200),
                    ReducerTriggerEvent = InMemoryChatHistoryProviderOptions.ChatReducerTriggerEvent.AfterMessageAdded,
                }),
            }, null, _sp);

            var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            var lastMessage = "";
            await foreach (var update in agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, task)], session, cancellationToken: ct))
            {
                if (!string.IsNullOrEmpty(update.Text))
                    lastMessage = update.Text.Trim();
            }
            return string.IsNullOrEmpty(lastMessage) ? "(no output)" : lastMessage;
        }
        catch (OperationCanceledException)
        {
            return "(cancelled)";
        }
        catch (Exception ex)
        {
            return $"(error: {ex.Message})";
        }
    }

    private static string TruncateForReport(string text, int maxChars)
        => text.Length <= maxChars ? text : text[..maxChars] + "\n\n... (truncated)";

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
        int capturedSpawn;
        lock (_budgetLock)
        {
            capturedSpawn = Interlocked.Increment(ref _spawnCount);
            if (capturedSpawn > MaxSpawns)
            {
                Interlocked.Decrement(ref _spawnCount);
                return $"Error: Budget exceeded: {MaxSpawns} spawns (max {MaxSpawns}). " +
                    "Use direct tools instead of spawning more subagents.";
            }
            _totalTurns++;
            budgetHint = GetBudgetHint();
        }

        // traceId is recorded via the orchestration span

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
            var capturedType = type ?? "generic";

            var agent = new ChatClientAgent(_llm, new ChatClientAgentOptions
            {
                Name = $"subagent-{capturedSpawn}",
                Description = systemPrompt,
                ChatOptions = chatOptions,
                // F2: cap at 200 messages
                ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
                {
                    ChatReducer = new MaxMessageCountReducer(200),
                    ReducerTriggerEvent = InMemoryChatHistoryProviderOptions.ChatReducerTriggerEvent.AfterMessageAdded,
                }),
            }, null, _sp);

            var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            var messageBuf = new StringBuilder();
            var messages = new List<(string role, string content)>();

            // 触发用户消息事件
            OnSubagentMessage?.Invoke(capturedSpawn, "user", task);
            Message?.Invoke(capturedSpawn, "user", task);

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
                            Message?.Invoke(capturedSpawn, "assistant", msg);
                        }
                        if (c is FunctionResultContent frc)
                        {
                            var resultStr = frc.Result?.ToString() ?? "";
                            var preview = resultStr.Length > 80 ? resultStr[..80] + "..." : resultStr;
                            OnSubagentMessage?.Invoke(capturedSpawn, "assistant", $"  📄 {preview}");
                            Message?.Invoke(capturedSpawn, "assistant", $"  📄 {preview}");
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
                        Message?.Invoke(capturedSpawn, "assistant", text);
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
                Message?.Invoke(capturedSpawn, "assistant", finalText);
                messages.Add(("assistant", finalText));
            }

            var elapsed = sw.ElapsedMilliseconds;
            var resultText = messages.Count > 0 ? messages[^1].Item2 : "(no output)";

            var output = ContentTruncator.Truncate(resultText, 8000);

            OnSubagentComplete?.Invoke(capturedSpawn);
            Completed?.Invoke(capturedSpawn);
            return budgetHint != null ? $"{output}\n{budgetHint}" : output;
        }
        catch (OperationCanceledException)
        {
            return "Error: Subagent cancelled by user";
        }
        catch (Exception ex)
        {
            return $"Error: Subagent failed: {ex.Message}";
        }
    }

    private List<AITool> FilterTools(bool readOnly)
    {
        return _allTools.Where(t =>
        {
            var name = t.Name ?? "";
            if (DeniedTools.Any(d => name.Equals(d, StringComparison.OrdinalIgnoreCase)))
                return false;

            // Check [ToolPermission] attribute for declarative permission scoping
            ToolPermission? declPerm = null;
            if (t is AIFunction func && func.UnderlyingMethod != null)
            {
                var methodAttr = func.UnderlyingMethod.GetCustomAttribute<ToolPermissionAttribute>(false);
                declPerm = methodAttr?.Required;
                if (declPerm == null)
                {
                    var classAttr = func.UnderlyingMethod.DeclaringType?
                        .GetCustomAttribute<ToolPermissionAttribute>(false);
                    declPerm = classAttr?.Required;
                }
            }

            if (readOnly)
            {
                // [ReadOnlyTool] attribute — explicit safe-for-read-only marker
                if (t is AIFunction roFunc && roFunc.UnderlyingMethod != null)
                {
                    if (roFunc.UnderlyingMethod.GetCustomAttribute<ReadOnlyToolAttribute>() != null)
                        return true;
                }
                // [ToolPermission(Read)] — equally safe
                if (declPerm == ToolPermission.Read)
                    return true;
                // [ToolPermission(Writes...)] — denied in read-only mode
                if (declPerm != null)
                    return false;
                // Unknown tool → deny (safe default)
                return false;
            }
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
        "explore" => $"{_subagentBaseSystem.Value}\n\n#10 PACT: Use protocolized message format.\n"
            + "Output format: action=<explore|report> | state=<finding|done> | summary=<text>\n"
            + "You are an exploration subagent. Use read_file, search_content, directory_tree.",
        "research" => $"{_subagentBaseSystem.Value}\n\n#10 PACT: Use protocolized message format.\n"
            + "Output format: action=<research|synthesize> | state=<partial|final> | summary=<text>\n"
            + "You are a research subagent. Use web_search for external references, read_file/search_content for local verification.",
        "review" => $"{_subagentBaseSystem.Value}\n\n#10 PACT: Use protocolized message format.\n"
            + "Output: action=<review> | state=<finding|summary> | file=<path> | line=<num> | severity=<P0|P1|P2> | detail=<text>\n"
            + "You are a code review subagent following the Open Code Review deterministic-first approach.\n"
            + "1. FIRST call BuildReviewContext() to get file groups and rule matches.\n"
            + "2. Use GroupChanges() to understand file relationships.\n"
            + "3. Review EACH GROUP as a unit.\n"
            + "4. AFTER analysis, call ReflectReviewQuality() to check coverage.",
        "security_review" => $"{_subagentBaseSystem.Value}\n\n#10 PACT: Use protocolized message format.\n"
            + "Output: action=<audit> | state=<finding|summary> | cwe=<id> | severity=<P0|P1|P2> | detail=<text>\n"
            + "You are a security review subagent. Check injection, auth, secrets, deserialization, path traversal.",
        _ => _subagentBaseSystem.Value
    };
}
