using System.Diagnostics;
using LTAI.Core.System;
using LTAI.Knowledge.Core;
using LTAI.Knowledge.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Session;

public sealed class MctsConfig
{
    public double ExplorationConstant { get; set; } = 1.414;
    public int MaxSimulations { get; set; } = 50;
    public int MaxDepth { get; set; } = 10;
    public int MaxBranches { get; set; } = 4;
    public int RolloutDepth { get; set; } = 3;
    public double DiscountFactor { get; set; } = 0.95;
    public bool EnableVirtualLoss { get; set; } = true;
    public int VirtualLossCount { get; set; } = 3;
}

public sealed class MctsNode
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string State { get; set; } = "";
    public string? ParentAction { get; set; }
    public MctsNode? Parent { get; set; }
    public List<MctsNode> Children { get; set; } = new();

    public int Visits { get; set; }
    public double TotalValue { get; set; }
    public double PriorProbability { get; set; } = 1.0;
    public int VirtualLosses { get; set; }

    public double AverageValue => Visits > 0 ? TotalValue / Visits : 0;
    public bool IsTerminal { get; set; }
    public bool IsExpanded => Children.Count > 0;

    public double UcbScore(double parentVisits, double explorationConstant)
    {
        if (Visits == 0 && VirtualLosses == 0)
            return double.PositiveInfinity;

        var effectiveVisits = Visits + VirtualLosses;
        var exploitation = AverageValue * PriorProbability;
        var exploration = explorationConstant * Math.Sqrt(Math.Log(Math.Max(1, parentVisits)) / Math.Max(1, effectiveVisits));
        return exploitation + exploration;
    }

    public List<MctsNode> GetPath()
    {
        var path = new List<MctsNode>();
        var current = this;
        while (current != null)
        {
            path.Add(current);
            current = current.Parent;
        }
        path.Reverse();
        return path;
    }

    public List<string> GetReasoningChain()
    {
        return GetPath()
            .Where(n => !string.IsNullOrEmpty(n.ParentAction))
            .Select(n => n.ParentAction!)
            .ToList();
    }
}

public sealed record MctsResult
{
    public List<string> BestReasoningChain { get; init; } = new();
    public double BestValue { get; init; }
    public int NodesExplored { get; init; }
    public int Simulations { get; init; }
    public double ElapsedMs { get; init; }
    public MctsNode Root { get; init; } = new();
    public Dictionary<string, double> NodeDepthStats { get; init; } = new();
}

public sealed class MctsAgentReasoner
{
    private readonly IChatClient _chatClient;
    private readonly Prompting.PromptBuilder _promptBuilder;
    private readonly AgenticRAG _agenticRAG;
    private readonly ILogger<MctsAgentReasoner>? _logger;

    public MctsAgentReasoner(
        IChatClient chatClient,
        Prompting.PromptBuilder promptBuilder,
        AgenticRAG agenticRAG,
        ILogger<MctsAgentReasoner>? logger = null)
    {
        _chatClient = chatClient;
        _promptBuilder = promptBuilder;
        _agenticRAG = agenticRAG;
        _logger = logger;
    }

    public async Task<MctsResult> SearchAsync(
        string taskDescription,
        MctsConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        var cfg = config ?? new MctsConfig();
        var sw = Stopwatch.StartNew();

        var root = new MctsNode
        {
            State = taskDescription,
            PriorProbability = 1.0
        };

        int simulations = 0;
        for (; simulations < cfg.MaxSimulations; simulations++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var leaf = await Selection(root, cfg, cancellationToken);
            var expanded = await Expansion(leaf, cfg, cancellationToken);
            var value = await Simulation(expanded, cfg, cancellationToken);
            Backpropagation(expanded, value);

            if (cfg.EnableVirtualLoss)
                ReleaseVirtualLoss(leaf);
        }

        var bestChild = root.Children
            .OrderByDescending(c => c.AverageValue * c.Visits)
            .ThenByDescending(c => c.Visits)
            .FirstOrDefault();

        var reasoningChain = bestChild?.GetReasoningChain() ?? new List<string>();
        var bestValue = bestChild?.AverageValue ?? root.AverageValue;

        sw.Stop();

        _logger?.LogInformation(
            "MCTS: simulations={Sims} nodes={Nodes} bestValue={Value:F3} chain={ChainLen} {Ms}ms",
            simulations, CountNodes(root), bestValue, reasoningChain.Count, sw.ElapsedMilliseconds);

        return new MctsResult
        {
            BestReasoningChain = reasoningChain,
            BestValue = Math.Round(bestValue, 4),
            NodesExplored = CountNodes(root),
            Simulations = simulations,
            ElapsedMs = sw.ElapsedMilliseconds,
            Root = root,
            NodeDepthStats = ComputeDepthStats(root)
        };
    }

    private async Task<MctsNode> Selection(MctsNode node, MctsConfig cfg, CancellationToken ct)
    {
        var current = node;

        while (current.IsExpanded && !current.IsTerminal && current.GetPath().Count < cfg.MaxDepth)
        {
            var children = current.Children;

            if (cfg.EnableVirtualLoss)
            {
                var best = children
                    .OrderByDescending(c => c.UcbScore(current.Visits, cfg.ExplorationConstant))
                    .FirstOrDefault();

                if (best != null)
                {
                    best.VirtualLosses += cfg.VirtualLossCount;
                    current = best;
                    continue;
                }
            }

            var unvisited = children.FirstOrDefault(c => c.Visits == 0 && c.VirtualLosses == 0);
            if (unvisited != null)
                return unvisited;

            current = children.MaxBy(c => c.UcbScore(current.Visits, cfg.ExplorationConstant)) ?? current;
            if (current.Visits > 0 && current.Children.Count == 0)
                break;
        }

        return current;
    }

    private async Task<MctsNode> Expansion(MctsNode node, MctsConfig cfg, CancellationToken ct)
    {
        if (node.IsTerminal || node.GetPath().Count >= cfg.MaxDepth)
            return node;

        if (node.Children.Count > 0)
            return node;

        var candidateActions = await GenerateCandidateActions(node, cfg.MaxBranches, ct);

        foreach (var action in candidateActions)
        {
            var childState = $"{node.State}\n[Step]: {action}";
            var child = new MctsNode
            {
                State = childState,
                Parent = node,
                ParentAction = action,
                PriorProbability = 1.0 / Math.Max(1, candidateActions.Count)
            };

            if (IsTerminalState(action))
                child.IsTerminal = true;

            node.Children.Add(child);
        }

        if (node.Children.Count == 0)
        {
            var fallbackChild = new MctsNode
            {
                State = node.State + "\n[Complete]",
                Parent = node,
                ParentAction = "任务完成",
                PriorProbability = 1.0,
                IsTerminal = true
            };
            node.Children.Add(fallbackChild);
        }

        return node.Children.OrderBy(c => c.Visits).FirstOrDefault(c => !c.IsTerminal) ?? node.Children[0];
    }

    private async Task<double> Simulation(MctsNode node, MctsConfig cfg, CancellationToken ct)
    {
        if (node.IsTerminal)
            return EvaluateTerminal(node, cfg);

        var current = node;
        var depth = 0;
        double cumulativeReward = 0;

        while (depth < cfg.RolloutDepth && !current.IsTerminal && !ct.IsCancellationRequested)
        {
            var actions = await GenerateCandidateActions(current, Math.Min(2, cfg.MaxBranches), ct);

            if (actions.Count == 0 || IsTerminalState(actions[0]))
            {
                cumulativeReward += EvaluateFinalState(current);
                break;
            }

            var chosenAction = actions[0];
            var stepReward = EvaluateStepQuality(chosenAction, current.State, depth);

            cumulativeReward += stepReward * Math.Pow(cfg.DiscountFactor, depth);

            current = new MctsNode
            {
                State = $"{current.State}\n[Rollout]: {chosenAction}",
                ParentAction = chosenAction
            };

            depth++;
        }

        return cumulativeReward / Math.Max(1, depth);
    }

    private void Backpropagation(MctsNode node, double value)
    {
        var current = node;
        while (current != null)
        {
            current.Visits++;
            current.TotalValue += value;
            current = current.Parent;
        }
    }

    private void ReleaseVirtualLoss(MctsNode node)
    {
        var current = node;
        while (current != null)
        {
            if (current.VirtualLosses > 0)
                current.VirtualLosses--;
            current = current.Parent;
        }
    }

    private async Task<List<string>> GenerateCandidateActions(
        MctsNode node, int maxBranches, CancellationToken ct)
    {
        try
        {
            var docs = _agenticRAG.Search(node.State, RAGMode.Iterative, maxRounds: 1);

            var opts = new Prompting.PromptBuildOptions
            {
                Domain = "mcts_reasoning",
                MaxContextTokens = 2000,
                IncludeStrategyHint = false,
                IncludeGlossary = false
            };

            var prompt = await _promptBuilder.BuildSinglePrompt(
                $"Based on the current state, generate {maxBranches} distinct next actions or reasoning steps.\n\nCurrent state:\n{node.State[..Math.Min(1000, node.State.Length)]}",
                docs, opts);

            var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: ct);
            return ParseActions(response.Text ?? "", maxBranches);
        }
        catch
        {
            return new List<string> { "分析当前状态并总结关键点" };
        }
    }

    private static List<string> ParseActions(string response, int maxBranches)
    {
        var actions = new List<string>();
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("• "))
                trimmed = trimmed[2..].Trim();
            else if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+[\.\)]\s"))
                trimmed = System.Text.RegularExpressions.Regex.Replace(trimmed, @"^\d+[\.\)]\s+", "");

            if (trimmed.Length < 3 || trimmed.Length > 300)
                continue;

            if (trimmed.StartsWith("THOUGHT:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("OBSERVATION:", StringComparison.OrdinalIgnoreCase))
                continue;

            actions.Add(trimmed);
            if (actions.Count >= maxBranches)
                break;
        }

        if (actions.Count == 0)
        {
            var sentences = System.Text.RegularExpressions.Regex.Split(response, @"(?<=[。.!！?？\n])")
                .Select(s => s.Trim())
                .Where(s => s.Length >= 5 && s.Length <= 300)
                .Take(maxBranches)
                .ToList();

            actions.AddRange(sentences);
        }

        return actions.Take(maxBranches).ToList();
    }

    private static bool IsTerminalState(string action)
    {
        var terminalKeywords = new[]
        {
            "任务完成", "答案:", "结论:", "总结:", "综上所述",
            "completed", "done", "final answer", "conclusion",
            "FINISH", "TERMINATE", "COMPLETE"
        };

        return terminalKeywords.Any(k =>
            action.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static double EvaluateTerminal(MctsNode node, MctsConfig cfg)
    {
        var state = node.State;
        var score = 0.5;

        if (state.Contains("答案:", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("结论:", StringComparison.OrdinalIgnoreCase))
            score += 0.3;

        if (state.Contains("错误", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("error", StringComparison.OrdinalIgnoreCase))
            score -= 0.2;

        var pathLength = node.GetPath().Count;
        if (pathLength > 1 && pathLength <= cfg.MaxDepth / 2)
            score += 0.1;

        return Math.Clamp(score, 0, 1);
    }

    private static double EvaluateFinalState(MctsNode node)
    {
        var depth = node.GetPath().Count;
        var baseReward = 0.3;

        if (depth > 3) baseReward += 0.1;
        if (depth > 5) baseReward += 0.1;

        var state = node.State;
        if (state.Contains("分析", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("评估", StringComparison.OrdinalIgnoreCase))
            baseReward += 0.15;

        return Math.Clamp(baseReward, 0, 1);
    }

    private static double EvaluateStepQuality(string action, string state, int depth)
    {
        var score = 0.3;

        if (action.Contains("搜索", StringComparison.OrdinalIgnoreCase) ||
            action.Contains("search", StringComparison.OrdinalIgnoreCase))
            score += 0.15;

        if (action.Contains("分析", StringComparison.OrdinalIgnoreCase) ||
            action.Contains("评估", StringComparison.OrdinalIgnoreCase))
            score += 0.2;

        if (action.Contains("计算", StringComparison.OrdinalIgnoreCase) ||
            action.Contains("对比", StringComparison.OrdinalIgnoreCase))
            score += 0.15;

        if (action.Length > 50 && action.Length < 200)
            score += 0.1;

        score -= depth * 0.02;

        return Math.Clamp(score, 0, 1);
    }

    private static int CountNodes(MctsNode root)
    {
        int count = 1;
        foreach (var child in root.Children)
            count += CountNodes(child);
        return count;
    }

    private static Dictionary<string, double> ComputeDepthStats(MctsNode root)
    {
        var depthNodes = new Dictionary<int, List<MctsNode>>();
        CollectByDepth(root, 0, depthNodes);

        return depthNodes.ToDictionary(
            kv => $"depth_{kv.Key}",
            kv => kv.Value.Count > 0 ? Math.Round(kv.Value.Average(n => n.AverageValue), 3) : 0.0);
    }

    private static void CollectByDepth(MctsNode node, int depth, Dictionary<int, List<MctsNode>> acc)
    {
        if (!acc.ContainsKey(depth))
            acc[depth] = new List<MctsNode>();

        acc[depth].Add(node);

        foreach (var child in node.Children)
            CollectByDepth(child, depth + 1, acc);
    }

    public string VisualizeTree(MctsNode root, int maxDepth = 3)
    {
        var lines = new List<string> { $"## MCTS Tree (Root: {root.State[..Math.Min(60, root.State.Length)]}...)" };

        void Traverse(MctsNode node, string prefix, int depth)
        {
            if (depth > maxDepth) return;

            foreach (var child in node.Children)
            {
                var action = (child.ParentAction ?? "(root)")[..Math.Min(50, (child.ParentAction ?? "").Length)];
                var line = $"{prefix}|- [{child.Visits}v / {child.AverageValue:F3}] {action}";
                if (child.IsTerminal) line += " [TERMINAL]";
                lines.Add(line);

                Traverse(child, prefix + "  ", depth + 1);
            }
        }

        Traverse(root, "", 0);
        return string.Join("\n", lines);
    }
}

