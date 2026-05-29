using System.Runtime.CompilerServices;
using System.Text.Json;
using LTAI.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Agents;

public sealed class ReasoningAgent : BaseAgent
{
    private readonly int _maxSearchDepth;
    private readonly int _maxIterations;
    private readonly int _maxTokensPerRequest;
    private readonly LTAI.Core.Governors.MemoryGraph? _memoryGraph;
    private const double ExplorationConstant = 1.414;

    public ReasoningAgent(
        LTAIAgentCard card,
        IChatClient brain,
        SkillRegistry skills,
        ILogger<ReasoningAgent> logger,
        LTAI.Core.Governors.MemoryGraph? memoryGraph = null)
        : base(card, brain, skills, logger)
    {
        _memoryGraph = memoryGraph;
        _maxSearchDepth = card.Options.TryGetValue("maxSearchDepth", out var d) && d is int depth ? depth : 5;
        _maxIterations = card.Options.TryGetValue("maxIterations", out var it) && it is int iterations ? iterations : 20;
        _maxTokensPerRequest = card.Options.TryGetValue("maxTokensPerRequest", out var mt) && mt is int maxTok ? maxTok : 8000;
    }

    protected override async Task<AgentResponse> ExecuteLogicAsync(
        AgentContext context, CancellationToken ct)
    {
        var msgList = context.FullHistory;
        var query = context.UserQuery;
        _logger.LogInformation("ReasoningAgent [{Name}]: MCTS reasoning depth={D} iter={I}", Name, _maxSearchDepth, _maxIterations);

        if (query.Length < 20)
            return await CallBrainAsync(msgList, ct: ct).ConfigureAwait(false);

        var result = await ExecuteMctsAsync(query, context.Session, ct).ConfigureAwait(false);

        if (result.Length < 100 || result.Contains("0%", StringComparison.Ordinal))
        {
            var scResult = await SelfConsistencyCheckAsync(query, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(scResult))
                result += scResult;
        }

        return new AgentResponse(new ChatMessage(ChatRole.Assistant, result));
    }

    private async Task<string> ExecuteMctsAsync(string query, AgentSession? session, CancellationToken ct)
    {
        var root = new MctsNode { State = query, Depth = 0, VisitCount = 1, TotalValue = 0.5 };
        int accumulatedTokens = 0;

        var subProblems = await DecomposeAsync(query, session, ct).ConfigureAwait(false);
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

            var expansion = await ExpandAsync(node, session, ct).ConfigureAwait(false);
            accumulatedTokens += EstimateTokens(expansion);
            if (!string.IsNullOrWhiteSpace(expansion))
            {
                var child = new MctsNode { State = expansion, Depth = node.Depth + 1, Parent = node };
                node.Children.Add(child);
                var simValue = await SimulateAsync(child, query, session, ct).ConfigureAwait(false);
                accumulatedTokens += EstimateTokens(simValue.ToString("F2"));
                Backpropagate(child, simValue);
            }
        }

        _logger.LogInformation("ReasoningAgent [{Name}]: MCTS complete, tokens={Tokens}/{Max}",
            Name, accumulatedTokens, _maxTokensPerRequest);

        if (_memoryGraph != null)
        {
            try
            {
                var bestPath = GetBestPath(root);
                var summary = $"MCTS: {query[..Math.Min(query.Length, 80)]}";
                _memoryGraph.AddNode(new LTAI.Core.Governors.MemoryNode
                {
                    Content = summary,
                    Summary = bestPath,
                    Domain = "reasoning",
                    LayerLevel = 1,
                    Importance = 0.6,
                    Tags = new HashSet<string> { "mcts", $"depth{root.Children.Count}" }
                });
            }
            catch
            {
                // Memory store failure is non-fatal — MCTS result is still returned
            }
        }

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
        var r = await CallBrainAsync(
            [new(ChatRole.User, $"Decompose into max {_maxSearchDepth} sub-problems, each prefixed \"- \":\n{q}")],
            ct: ct);
        return (r.Text ?? "").Split('\n').Where(l => l.TrimStart().StartsWith("-")).Select(l => l.TrimStart().TrimStart('-').Trim()).Where(l => l.Length > 3).ToList();
    }

    private async Task<string> ExpandAsync(MctsNode n, AgentSession? s, CancellationToken ct)
    {
        var r = await CallBrainAsync(
            [new(ChatRole.User, $"Propose ONE concrete next step for:\n{n.State}\n\nNext step:")],
            ct: ct);
        return r.Text?.Trim() ?? "";
    }

    private async Task<double> SimulateAsync(MctsNode n, string orig, AgentSession? s, CancellationToken ct)
    {
        var r = await CallBrainAsync(
            [new(ChatRole.User, $"Rate relevance to \"{orig}\":\n{n.State}\nScore (0.0-1.0):")],
            ct: ct);
        return double.TryParse(r.Text?.Trim(), out var v) ? Math.Clamp(v, 0, 1) : 0.5;
    }

    private async Task<string> SelfConsistencyCheckAsync(string query, CancellationToken ct)
    {
        try
        {
            var samples = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                var temp = 0.3 + i * 0.3;
                var response = await CallBrainAsync(
                    [new(ChatRole.User, query)],
                    ct: ct).ConfigureAwait(false);
                var text = response?.Text?.Trim() ?? "";
                if (text.Length > 10)
                    samples.Add(text[..Math.Min(text.Length, 200)]);
            }

            if (samples.Count < 2) return "";

            var allSimilar = true;
            for (int i = 1; i < samples.Count; i++)
            {
                var overlap = samples[i].Split(' ').Intersect(samples[0].Split(' ')).Count();
                if (overlap < 3) { allSimilar = false; break; }
            }

            if (!allSimilar)
                return "\n\n⚠️ Self-consistency check: responses diverged. Confidence may be low.";
        }
        catch
        {
            // Self-consistency check failure is non-fatal — return empty warning
        }
        return "";
    }

    private static string GetBestPath(MctsNode root)
    {
        var path = new List<string>();
        var cur = root;
        while (cur.Children.Count > 0)
        {
            cur = cur.Children.OrderByDescending(c => c.TotalValue / Math.Max(c.VisitCount, 1)).First();
            path.Add(cur.State.Length > 120 ? cur.State[..120] + "..." : cur.State);
            if (path.Count >= 5) break;
        }
        return string.Join(" → ", path);
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
        var r = await ExecuteLogicAsync(
            new AgentContext(messages.LastOrDefault()?.Text ?? "", messages.ToList(), session), cancellationToken);
        var t = r.Text ?? "";
        for (int i = 0; i < t.Length; i += 80)
            yield return new AgentResponseUpdate(new ChatResponseUpdate(ChatRole.Assistant, t[i..Math.Min(i + 80, t.Length)]));
    }
}
