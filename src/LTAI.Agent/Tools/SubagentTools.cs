using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Tools;

/// <summary>
/// Subagent tools with: budget tracking, tool restriction, structured returns.
/// Ported from DeepSeek-Reasonix subagent.ts patterns.
/// </summary>
public sealed class SubagentTools
{
    private readonly IServiceProvider _sp;
    private readonly IChatClient _llm;
    private readonly string _ws;

    // Session-level budget tracking
    private int _spawnCount;
    private int _totalTurns;
    private readonly object _budgetLock = new();

    // Hard limits
    private const int MaxSpawns = 10;
    private const int TotalTurnLimit = 50;

    private static readonly string SubagentBaseSystem = """
        You are an LTAI subagent. The parent agent spawned you for one focused subtask.

        Rules:
        - Stay on task. Do not expand scope.
        - Your final message is all the parent sees. Make it complete and self-contained.
        - No follow-up offers, no "let me know if you need more."
        - Prefer a clear, distilled answer over a long log.
        - Do NOT call spawn_subagent (you are already a subagent — that would create a recursive loop).
        """;

    // Subagent types that restrict to read-only tools
    private static readonly HashSet<string> ReadOnlyTypes = ["explore", "review", "security_review"];

    public SubagentTools(IServiceProvider sp, IChatClient llm, string ws)
    {
        _sp = sp;
        _llm = llm;
        _ws = ws;
    }

    [Description("Explore codebase: wide-net read-only investigation, returns a distilled conclusion")]
    public Task<string> Explore(
        [Description("Concrete investigation question")] string task,
        CancellationToken ct = default)
        => SpawnAsync(task, "explore", ct: ct);

    [Description("Research: combine web search with code reading, returns synthesis")]
    public Task<string> Research(
        [Description("Research question requiring web + code")] string task,
        CancellationToken ct = default)
        => SpawnAsync(task, "research", ct: ct);

    [Description("Review code changes: flags correctness/security/missing-tests")]
    public Task<string> Review(
        [Description("Focus area or 'general'")] string task,
        CancellationToken ct = default)
        => SpawnAsync(task, "review", ct: ct);

    [Description("Security review: injection/auth/secrets/deserialization")]
    public Task<string> SecurityReview(
        [Description("Scope hint or 'full'")] string task,
        CancellationToken ct = default)
        => SpawnAsync(task, "security_review", ct: ct);

    [Description("Spawn an isolated subagent. Each spawn pays a prefix-cache miss + full child loop — prefer direct tools.")]
    public Task<string> SpawnSubagent(
        [Description("The subtask to perform")] string task,
        [Description("Optional type: explore, research, review, security_review")] string? type = null,
        [Description("Optional system prompt override")] string? system = null,
        CancellationToken ct = default)
        => SpawnAsync(task, type, system, ct);

    private async Task<string> SpawnAsync(string task, string? type, string? systemOverride = null, CancellationToken ct = default)
    {
        // ── Budget check ──
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

        var sw = Stopwatch.StartNew();
        var systemPrompt = systemOverride ?? GetSystemPrompt(type ?? "generic");
        var isReadOnly = ReadOnlyTypes.Contains(type ?? "");

        if (isReadOnly)
            systemPrompt += "\n\nIMPORTANT: You are in read-only mode. Use only read_file, search_content, directory_tree, list_files, glob. Do NOT write/edit/delete any files.";

        try
        {
            var agent = new ChatClientAgent(_llm, new ChatClientAgentOptions
            {
                Name = $"subagent-{_spawnCount}",
                Description = systemPrompt,
                ChatOptions = new ChatOptions { Temperature = 0.3f, MaxOutputTokens = 4096 },
                ChatHistoryProvider = new InMemoryChatHistoryProvider(),
            }, null, _sp);

            ct.ThrowIfCancellationRequested(); // parent abort check
            var session = await agent.CreateSessionAsync(ct);
            ct.ThrowIfCancellationRequested();
            var response = await agent.RunAsync([new ChatMessage(ChatRole.User, task)], session, cancellationToken: ct);
            var result = response.Messages?.LastOrDefault()?.Text ?? "(no output)";

            var elapsed = sw.ElapsedMilliseconds;
            var output = JsonSerializer.Serialize(new
            {
                success = true,
                output = Truncate(result, 8000),
                spawnCount = _spawnCount,
                elapsedMs = elapsed,
                type = type ?? "generic",
            });

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
