using LTAI.AI;
using LTAI.Core.Configuration;
using LTAI.Agent.Pipeline;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Execution;

public sealed record DFSDTState
{
    public string Query { get; init; } = "";
    public List<string> Conversation { get; init; } = [];
    public List<(string Tool, string Args, string Result)> Results { get; init; } = [];
    public int Depth { get; init; }
}

public sealed record DFSDTResult
{
    public string? Answer { get; init; }
    public List<string> Path { get; init; } = [];
    public bool Success { get; init; }
    public int NodesExplored { get; init; }
}

public sealed class DFSDToolExecutor
{
    private readonly IChatClient? _llm;
    private readonly IToolRegistry _toolRegistry;
    private readonly int _maxDepth;
    private readonly int _maxNodes;
    private readonly ILogger<DFSDToolExecutor> _logger;

    public DFSDToolExecutor(
        IToolRegistry toolRegistry,
        IChatClient? llm = null,
        ILogger<DFSDToolExecutor>? logger = null,
        int? maxDepth = null,
        int? maxNodes = null)
    {
        _toolRegistry = toolRegistry;
        _llm = llm;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DFSDToolExecutor>.Instance;
        _maxDepth = maxDepth ?? EnvironmentConfig.DfsdMaxDepth;
        _maxNodes = maxNodes ?? EnvironmentConfig.DfsdMaxNodes;
    }

    public async Task<DFSDTResult> ExecuteAsync(string query, CancellationToken ct)
    {
        if (_llm == null)
            return new DFSDTResult { Success = false, Answer = "No LLM configured" };

        var root = new DFSDTState { Query = query };
        var path = new List<string>();
        var counter = new NodeCounter();
        var visited = new HashSet<string>();

        var result = await DfsAsync(root, path, visited, counter, ct).ConfigureAwait(false);
        return result with { NodesExplored = counter.Value };
    }

    private sealed class NodeCounter { public int Value; }

    private async Task<DFSDTResult> DfsAsync(
        DFSDTState state, List<string> path, HashSet<string> visited,
        NodeCounter nodesExplored, CancellationToken ct)
    {
        if (state.Depth >= _maxDepth || nodesExplored.Value >= _maxNodes)
        {
            return new DFSDTResult
            {
                Success = false,
                Path = [..path],
                Answer = "Reached max depth or node limit",
            };
        }

        var stateKey = BuildStateKey(state);
        if (!visited.Add(stateKey)) return new DFSDTResult { Success = false, Path = [..path] };

        nodesExplored.Value++;

        var action = await ThinkAndActAsync(state, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(action)) return new DFSDTResult { Success = false, Path = [..path] };

        path.Add(action);

        if (IsFinalAnswer(action, out var answer))
            return new DFSDTResult { Success = true, Path = [..path], Answer = answer };

        var nextState = await ExecuteActionAsync(state, action, ct).ConfigureAwait(false);
        if (nextState == null)
        {
            path.RemoveAt(path.Count - 1);
            return new DFSDTResult { Success = false, Path = [..path] };
        }

        var result = await DfsAsync(nextState, path, visited, nodesExplored, ct).ConfigureAwait(false);
        if (result.Success) return result;

        path.RemoveAt(path.Count - 1);
        return new DFSDTResult { Success = false, Path = [..path], Answer = $"Action '{action}' failed, backtracking" };
    }

    private async Task<string> ThinkAndActAsync(DFSDTState state, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Current task: " + state.Query);
        if (state.Results.Count > 0)
        {
            sb.AppendLine("Previous tool results:");
            foreach (var (tool, args, result) in state.Results)
            {
                var snippet = result.Length > 200 ? result[..200] + "..." : result;
                sb.AppendLine($"  {tool}({args}) -> {snippet}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("What should you do next? Options:");
        sb.AppendLine("1. Call a tool: TOOL: ToolName(arg1=val1, ...)");
        sb.AppendLine("2. Provide final answer: FINAL: <your answer>");
        sb.AppendLine("3. Abandon task: ABANDON: <reason>");

        try
        {
            var response = await _llm.GetResponseAsync(
                [new ChatMessage(ChatRole.User, sb.ToString())], null, ct).ConfigureAwait(false);
            return response.Text?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool IsFinalAnswer(string action, out string? answer)
    {
        if (action.StartsWith("FINAL:", StringComparison.OrdinalIgnoreCase))
        {
            answer = action["FINAL:".Length..].Trim();
            return true;
        }
        answer = null;
        return false;
    }

    private async Task<DFSDTState?> ExecuteActionAsync(DFSDTState state, string action, CancellationToken ct)
    {
        if (!action.StartsWith("TOOL:", StringComparison.OrdinalIgnoreCase))
            return null;

        var toolCall = action["TOOL:".Length..].Trim();
        var parenIdx = toolCall.IndexOf('(');
        if (parenIdx < 0) return null;

        var toolName = toolCall[..parenIdx].Trim();
        var argsText = parenIdx + 1 < toolCall.Length && toolCall[^1] == ')'
            ? toolCall[(parenIdx + 1)..^1].Trim()
            : "";

        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in argsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eqIdx = pair.IndexOf('=');
            if (eqIdx > 0) args[pair[..eqIdx].Trim()] = pair[(eqIdx + 1)..].Trim().Trim('"');
        }

        string result;
        try
        {
            var funcResult = await _toolRegistry.InvokeToolAsync(toolName, args, ct).ConfigureAwait(false);
            result = funcResult ?? "[tool returned no result]";
        }
        catch (Exception ex)
        {
            result = $"Error: {ex.Message}";
            _logger.LogWarning(ex, "DFSDT: tool '{Name}' failed", toolName);
        }

        var newResults = new List<(string, string, string)>(state.Results) { (toolName, argsText, result) };

        return new DFSDTState
        {
            Query = state.Query,
            Conversation = [..state.Conversation, action],
            Results = newResults,
            Depth = state.Depth + 1,
        };
    }

    private static string BuildStateKey(DFSDTState state)
    {
        return $"{state.Query}|{string.Join("|", state.Results.Select(r => $"{r.Tool}:{r.Args}"))}";
    }
}
