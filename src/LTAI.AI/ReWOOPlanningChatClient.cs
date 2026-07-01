using System.Runtime.CompilerServices;
using System.Text;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI;

public sealed class ReWOOPlanningChatClient : DelegatingChatClient
{
    private readonly IChatClient? _planner;
    private readonly IChatClient? _solver;
    private readonly IToolRegistry _toolRegistry;
    private readonly ILogger<ReWOOPlanningChatClient> _logger;

    public ReWOOPlanningChatClient(
        IChatClient? inner,
        IChatClient? planner,
        IChatClient? solver,
        ILogger<ReWOOPlanningChatClient>? logger = null,
        IToolRegistry? toolRegistry = null)
        : base(inner ?? throw new ArgumentNullException(nameof(inner)))
    {
        _planner = planner;
        _solver = solver;
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ReWOOPlanningChatClient>.Instance;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var msgList = messages.ToList();
        if (_planner == null || _solver == null || !ShouldPlan(msgList))
            return await base.GetResponseAsync(msgList, options, ct).ConfigureAwait(false);

        var query = ExtractQuery(msgList);

        var plan = await GeneratePlanAsync(query, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(plan))
            return await base.GetResponseAsync(msgList, options, ct).ConfigureAwait(false);

        var observations = await ExecutePlanAsync(plan, query, ct).ConfigureAwait(false);

        var result = await SolveAsync(query, plan, observations, options, ct).ConfigureAwait(false);
        return result;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await GetResponseAsync(messages, options, ct).ConfigureAwait(false);
        if (response.Text is { Length: > 0 } text)
        {
            foreach (var chunk in ChunkText(text))
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }
    }

    private static bool ShouldPlan(List<ChatMessage> messages)
    {
        if (!EnvironmentConfig.ReWooEnabled) return false;
        var lastUser = messages.LastOrDefault(m => m.Role == ChatRole.User);
        if (lastUser?.Text == null) return false;
        var t = lastUser.Text;
        return t.Length > 30 && !t.Contains("/no-plan", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractQuery(List<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            if (m.Role == ChatRole.User && m.Text != null)
                sb.AppendLine(m.Text);
        }
        return sb.ToString().Trim();
    }

    private async Task<string> GeneratePlanAsync(string query, CancellationToken ct)
    {
        var prompt = $@"You are a planner. Given a task, create a numbered plan of tool calls.
Each step must use the format: #E[N] ToolName(arg1=val1, arg2=val2)
Use these tools: ReadFileContent, SearchContent, Glob, Write, Edit, SafeShell, PatchEdit
Output ONLY the plan, no explanation.

Task: {query}";
        try
        {
            var response = await _planner.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)], null, ct).ConfigureAwait(false);
            return response.Text ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReWOO planner failed");
            return "";
        }
    }

    private async Task<string> ExecutePlanAsync(string plan, string query, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Execution Results");
        sb.AppendLine();

        var lines = plan.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.Contains("#E[")) continue;

            var toolCall = ParseToolCall(trimmed);
            if (toolCall == null) continue;

            sb.AppendLine($"### {toolCall.StepId}: {toolCall.ToolName}({string.Join(", ", toolCall.Args.Select(kv => $"{kv.Key}={kv.Value}"))})");

            var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in toolCall.Args)
                args[kv.Key] = kv.Value;

            try
            {
                var result = await _toolRegistry.InvokeToolAsync(toolCall.ToolName, args, ct)
                    .ConfigureAwait(false);
                var snippet = result ?? "[tool returned no result]";
                if (snippet.Length > 500) snippet = snippet[..500] + "\n... [truncated]";
                sb.AppendLine($"*Observation for {toolCall.StepId}:*");
                sb.AppendLine($"```\n{snippet}\n```");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"*Observation for {toolCall.StepId}:*");
                sb.AppendLine($"`Error: {ex.Message}`");
                _logger.LogWarning(ex, "ReWOO: tool '{Name}' failed", toolCall.ToolName);
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private async Task<ChatResponse> SolveAsync(string query, string plan, string observations,
        ChatOptions? options, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Original Task");
        sb.AppendLine(query);
        sb.AppendLine();
        sb.AppendLine("## Plan");
        sb.AppendLine(plan);
        sb.AppendLine();
        sb.AppendLine("## Execution Results");
        sb.AppendLine(observations);
        sb.AppendLine();
        sb.AppendLine("Based on the plan and results above, produce the final answer.");

        try
        {
            var resp = await _solver.GetResponseAsync(
                [new ChatMessage(ChatRole.User, sb.ToString())], options, ct).ConfigureAwait(false);
            return resp;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReWOO solver failed, falling back to inner client");
            return await base.GetResponseAsync(
                [new ChatMessage(ChatRole.User, query)], options, ct).ConfigureAwait(false);
        }
    }

    private sealed record ToolCallInfo(string StepId, string ToolName, Dictionary<string, string> Args);

    private static ToolCallInfo? ParseToolCall(string line)
    {
        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @"#E\[(\d+)\]\s*(\w+)\s*\((.*)\)");
            if (!match.Success) return null;

            var stepId = $"E{match.Groups[1].Value}";
            var toolName = match.Groups[2].Value;
            var argsText = match.Groups[3].Value;

            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var arg in argsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eqIdx = arg.IndexOf('=');
                if (eqIdx > 0)
                    args[arg[..eqIdx].Trim()] = arg[(eqIdx + 1)..].Trim().Trim('"');
            }

            return new ToolCallInfo(stepId, toolName, args);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ChunkText(string text)
    {
        const int chunkSize = 100;
        for (int i = 0; i < text.Length; i += chunkSize)
            yield return text.Substring(i, Math.Min(chunkSize, text.Length - i));
    }
}
