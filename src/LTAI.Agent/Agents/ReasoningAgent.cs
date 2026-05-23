using System.Runtime.CompilerServices;
using System.Text.Json;
using LTAI.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Agents;

public sealed class ReasoningAgent : AIAgent
{
    private readonly ChatClientAgent _inner;
    private readonly ILogger<ReasoningAgent> _logger;
    private readonly int _maxSearchDepth;
    private readonly int _maxIterations;
    private readonly int _maxTokensPerRequest;
    private const double ExplorationConstant = 1.414;

    public override string Name { get; }
    public override string Description { get; }

    public ReasoningAgent(
        IChatClient chatClient,
        LTAIAgentCard card,
        IEnumerable<Microsoft.Extensions.AI.AITool> reasoningTools,
        ILogger<ReasoningAgent> logger)
    {
        _logger = logger;
        Name = card.Name;
        Description = card.Instructions;

        _maxSearchDepth = card.Options.TryGetValue("maxSearchDepth", out var d) && d is int depth ? depth : 5;
        _maxIterations = card.Options.TryGetValue("maxIterations", out var it) && it is int iterations ? iterations : 20;
        _maxTokensPerRequest = card.Options.TryGetValue("maxTokensPerRequest", out var mt) && mt is int maxTok ? maxTok : 8000;

        _inner = chatClient.AsBuilder().BuildAIAgent(new ChatClientAgentOptions
        {
            Name = card.Name,
            Description = card.Instructions,
            ChatOptions = new() { Tools = reasoningTools.ToList() }
        });
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);
        if (userMsg is null)
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, "No reasoning request received."));

        var query = userMsg.Text ?? "";
        _logger.LogInformation("ReasoningAgent [{Name}]: MCTS reasoning depth={D} iter={I}", Name, _maxSearchDepth, _maxIterations);

        if (query.Length < 20)
            return await _inner.RunAsync(messages, session, options, cancellationToken);

        var result = await ExecuteMctsAsync(query, session, cancellationToken);
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, result));
    }

    private async Task<string> ExecuteMctsAsync(string query, AgentSession? session, CancellationToken ct)
    {
        var root = new MctsNode { State = query, Depth = 0, VisitCount = 1, TotalValue = 0.5 };
        int accumulatedTokens = 0;

        var subProblems = await DecomposeAsync(query, session, ct);
        accumulatedTokens += EstimateTokens(query) + subProblems.Sum(EstimateTokens);
        foreach (var sp in subProblems.Take(_maxSearchDepth))
            root.Children.Add(new MctsNode { State = sp, Depth = 1, Parent = root });

        for (int iter = 0; iter < _maxIterations; iter++)
        {
            ct.ThrowIfCancellationRequested();

            if (accumulatedTokens >= _maxTokensPerRequest)
            {
                _logger.LogWarning("ReasoningAgent [{Name}]: MCTS token budget exhausted ({Used}/{Max})",
                    Name, accumulatedTokens, _maxTokensPerRequest);
                break;
            }

            var node = Select(root);
            if (node.Depth >= _maxSearchDepth || node.IsTerminal) continue;

            var expansion = await ExpandAsync(node, session, ct);
            accumulatedTokens += EstimateTokens(expansion);
            if (!string.IsNullOrWhiteSpace(expansion))
            {
                var child = new MctsNode { State = expansion, Depth = node.Depth + 1, Parent = node };
                node.Children.Add(child);
                var simValue = await SimulateAsync(child, query, session, ct);
                accumulatedTokens += EstimateTokens(simValue.ToString("F2"));
                Backpropagate(child, simValue);
            }
        }

        _logger.LogInformation("ReasoningAgent [{Name}]: MCTS complete, tokens={Tokens}/{Max}",
            Name, accumulatedTokens, _maxTokensPerRequest);
        return BuildResult(root, root.Children.Sum(c => c.VisitCount));
    }

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int chinese = 0, ascii = 0;
        foreach (var ch in text)
        {
            if (ch >= 0x4E00 && ch <= 0x9FFF) chinese++;
            else ascii++;
        }
        return chinese + (ascii / 4);
    }

    private static MctsNode Select(MctsNode node)
    {
        while (node.Children.Count > 0)
            node = node.Children.OrderByDescending(c => Ucb1(c)).First();
        return node;
    }

    private static double Ucb1(MctsNode n)
    {
        if (n.VisitCount == 0) return double.MaxValue;
        if (n.Parent?.VisitCount is null or 0) return n.TotalValue / n.VisitCount;
        return n.TotalValue / n.VisitCount + ExplorationConstant * Math.Sqrt(Math.Log(n.Parent.VisitCount) / n.VisitCount);
    }

    private static void Backpropagate(MctsNode? node, double value)
    {
        for (; node != null; node = node.Parent)
        { node.VisitCount++; node.TotalValue += value; }
    }

    private async Task<List<string>> DecomposeAsync(string q, AgentSession? s, CancellationToken ct)
    {
        var r = await _inner.RunAsync(
            [new(ChatRole.User, $"Decompose into max {_maxSearchDepth} sub-problems, each prefixed \"- \":\n{q}")],
            s, null, ct);
        return (r.Text ?? "").Split('\n').Where(l => l.TrimStart().StartsWith("-")).Select(l => l.TrimStart().TrimStart('-').Trim()).Where(l => l.Length > 3).ToList();
    }

    private async Task<string> ExpandAsync(MctsNode n, AgentSession? s, CancellationToken ct)
    {
        var r = await _inner.RunAsync(
            [new(ChatRole.User, $"Propose ONE concrete next step for:\n{n.State}\n\nNext step:")],
            s, null, ct);
        return r.Text?.Trim() ?? "";
    }

    private async Task<double> SimulateAsync(MctsNode n, string orig, AgentSession? s, CancellationToken ct)
    {
        var r = await _inner.RunAsync(
            [new(ChatRole.User, $"Rate relevance to \"{orig}\":\n{n.State}\nScore (0.0-1.0):")],
            s, null, ct);
        return double.TryParse(r.Text?.Trim(), out var v) ? Math.Clamp(v, 0, 1) : 0.5;
    }

    private static string BuildResult(MctsNode root, int totalVisits)
    {
        var path = new List<MctsNode>(); var cur = root;
        while (cur.Children.Count > 0) { cur = cur.Children.OrderByDescending(c => c.TotalValue / Math.Max(c.VisitCount, 1)).First(); path.Add(cur); }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## MCTS Reasoning ({totalVisits} iterations, {root.Children.Count} branches)");
        for (int i = 0; i < path.Count; i++)
            sb.AppendLine($"{i + 1}. [{path[i].TotalValue / Math.Max(path[i].VisitCount, 1):P0}] {path[i].State}");
        return sb.ToString();
    }

    private sealed class MctsNode
    {
        public string State { get; init; } = "";
        public int Depth { get; init; }
        public int VisitCount { get; set; }
        public double TotalValue { get; set; }
        public MctsNode? Parent { get; init; }
        public List<MctsNode> Children { get; } = new();
        public bool IsTerminal => Depth >= 10;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var r = await RunCoreAsync(messages, session, options, cancellationToken);
        var t = r.Text ?? "";
        for (int i = 0; i < t.Length; i += 80)
            yield return new AgentResponseUpdate(ChatRole.Assistant, t[i..Math.Min(i + 80, t.Length)]);
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken ct = default)
        => _inner.CreateSessionAsync(ct);

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession s, JsonSerializerOptions? o = null, CancellationToken ct = default)
        => _inner.SerializeSessionAsync(s, o, ct);

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement j, JsonSerializerOptions? o = null, CancellationToken ct = default)
        => _inner.DeserializeSessionAsync(j, o, ct);
}
