using System.Collections.Concurrent;
using System.Diagnostics;
using LTAI.Vector.Knowledge;
using LTAI.Vector.Knowledge.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.TreeLLM.Session;

public sealed class ParallelNode
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Task { get; set; } = "";
    public string? Result { get; set; }
    public double Confidence { get; set; }
    public ParallelNode? Parent { get; set; }
    public List<ParallelNode> Children { get; set; } = new();
    public List<string> ParentIds { get; set; } = new();
    public NodeState State { get; set; } = NodeState.Pending;
    public int Depth { get; set; }
    public long ExecutionMs { get; set; }
    public List<string> Sources { get; set; } = new();

    public bool IsReady => ParentIds.Count == 0 ||
        (Parent != null && Parent.State == NodeState.Completed);
}

public enum NodeState { Pending, Running, Completed, Failed }

public sealed class ParallelGraphConfig
{
    public int MaxConcurrent { get; set; } = 8;
    public int MaxDepth { get; set; } = 5;
    public int MaxBranchesPerNode { get; set; } = 4;
    public int MaxTotalNodes { get; set; } = 50;
    public double MinConfidenceForBranch { get; set; } = 0.3;
    public bool EnableMerge { get; set; } = true;
    public bool TrackPerformance { get; set; } = true;
}

public sealed record ParallelGraphResult
{
    public string FinalAnswer { get; init; } = "";
    public List<ParallelNode> AllNodes { get; init; } = new();
    public int TotalNodes { get; init; }
    public int CompletedNodes { get; init; }
    public int FailedNodes { get; init; }
    public int MaxDepthReached { get; init; }
    public double ElapsedMs { get; init; }
    public List<string> AllSources { get; init; } = new();
    public Dictionary<string, double> BranchScores { get; init; } = new();
    public List<BranchDecision> BranchDecisions { get; init; } = new();
}

public sealed record BranchDecision
{
    public string NodeId { get; init; } = "";
    public string NodeTask { get; init; } = "";
    public int NumBranches { get; init; }
    public int Depth { get; init; }
    public double OutcomeReward { get; set; }
    public bool WasBeneficial { get; set; }
    public List<string> BranchResults { get; init; } = new();
}

public sealed class ParallelReasoningGraph
{
    private readonly IChatClient _chatClient;
    private readonly AgenticRAG _agenticRAG;
    private readonly Prompting.PromptBuilder _promptBuilder;
    private readonly ILogger<ParallelReasoningGraph>? _logger;

    private readonly ConcurrentDictionary<string, ParallelNode> _allNodes = new();
    private readonly List<BranchDecision> _branchDecisions = new();

    public ParallelReasoningGraph(
        IChatClient chatClient,
        AgenticRAG agenticRAG,
        Prompting.PromptBuilder promptBuilder,
        ILogger<ParallelReasoningGraph>? logger = null)
    {
        _chatClient = chatClient;
        _agenticRAG = agenticRAG;
        _promptBuilder = promptBuilder;
        _logger = logger;
    }

    public List<BranchDecision> GetBranchDecisions() => _branchDecisions.ToList();

    public async Task<ParallelGraphResult> ReasonAsync(
        string task,
        ParallelGraphConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        var cfg = config ?? new ParallelGraphConfig();
        var sw = Stopwatch.StartNew();

        var root = new ParallelNode
        {
            Task = task,
            Depth = 0,
            State = NodeState.Running
        };
        _allNodes[root.Id] = root;

        var semaphore = new SemaphoreSlim(cfg.MaxConcurrent);
        var pendingQueue = new ConcurrentQueue<ParallelNode>();
        pendingQueue.Enqueue(root);

        var runningTasks = new ConcurrentDictionary<string, Task>();
        int completedNodes = 0;
        int failedNodes = 0;
        int maxDepthReached = 0;

        while (!pendingQueue.IsEmpty || !runningTasks.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();

            while (pendingQueue.TryDequeue(out var node))
            {
                if (_allNodes.Count >= cfg.MaxTotalNodes) break;

                await semaphore.WaitAsync(cancellationToken);
                var t = ExecuteNodeAsync(node, cfg, semaphore, cancellationToken);
                runningTasks[node.Id] = t;
            }

            if (runningTasks.IsEmpty && pendingQueue.IsEmpty) break;

            var completedTask = await Task.WhenAny(runningTasks.Values);
            var completedEntry = runningTasks.First(kv => kv.Value == completedTask);
            runningTasks.TryRemove(completedEntry.Key, out _);

            if (_allNodes.TryGetValue(completedEntry.Key, out var completedNode))
            {
                if (completedNode.State == NodeState.Completed) completedNodes++;
                else if (completedNode.State == NodeState.Failed) failedNodes++;
                if (completedNode.Depth > maxDepthReached) maxDepthReached = completedNode.Depth;

                if (completedNode.State == NodeState.Completed &&
                    completedNode.Depth < cfg.MaxDepth &&
                    completedNode.Confidence >= cfg.MinConfidenceForBranch)
                {
                    var branches = await GenerateBranchesAsync(completedNode, cfg);
                    foreach (var branch in branches)
                    {
                        if (_allNodes.Count >= cfg.MaxTotalNodes) break;
                        pendingQueue.Enqueue(branch);
                    }

                    var decision = new BranchDecision
                    {
                        NodeId = completedNode.Id,
                        NodeTask = completedNode.Task[..Math.Min(100, completedNode.Task.Length)],
                        NumBranches = branches.Count,
                        Depth = completedNode.Depth,
                        BranchResults = new()
                    };
                    _branchDecisions.Add(decision);
                }
            }
        }

        sw.Stop();

        var mergeResult = cfg.EnableMerge
            ? await MergeResultsAsync(root, task, cfg, cancellationToken)
            : root.Result ?? "";

        var allSources = _allNodes.Values
            .SelectMany(n => n.Sources)
            .Distinct()
            .ToList();

        return new ParallelGraphResult
        {
            FinalAnswer = mergeResult,
            AllNodes = _allNodes.Values.OrderBy(n => n.Depth).ThenBy(n => n.Id).ToList(),
            TotalNodes = _allNodes.Count,
            CompletedNodes = completedNodes,
            FailedNodes = failedNodes,
            MaxDepthReached = maxDepthReached,
            ElapsedMs = sw.ElapsedMilliseconds,
            AllSources = allSources,
            BranchScores = _allNodes.Values
                .Where(n => n.State == NodeState.Completed)
                .ToDictionary(n => n.Id, n => n.Confidence),
            BranchDecisions = _branchDecisions.ToList()
        };
    }

    private async Task ExecuteNodeAsync(
        ParallelNode node,
        ParallelGraphConfig cfg,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            node.State = NodeState.Running;

            var parentContext = node.Parent?.Result is { Length: > 0 } pr
                ? $"Parent result: {pr[..Math.Min(300, pr.Length)]}"
                : "";

            var prompt = BuildNodePrompt(node.Task, parentContext, node.Depth);
            var docs = _agenticRAG.Search(node.Task, RAGMode.Iterative, maxRounds: 2);

            var builtPrompt = await _promptBuilder.BuildSinglePrompt(prompt, docs,
                new Prompting.PromptBuildOptions
                {
                    MaxContextTokens = 4000,
                    IncludeStrategyHint = false
                });

            var response = await _chatClient.GetResponseAsync(builtPrompt, cancellationToken: cancellationToken);
            var result = response.Text ?? "";
            node.Result = result;
            node.Confidence = EstimateConfidence(result);
            node.Sources = docs.Select(d => d.Source).Where(s => !string.IsNullOrEmpty(s)).Cast<string>().ToList();

            node.State = NodeState.Completed;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ParallelNode {NodeId} failed", node.Id);
            node.State = NodeState.Failed;
            node.Result = $"[Failed: {ex.Message}]";
            node.Confidence = 0;
        }
        finally
        {
            node.ExecutionMs = sw.ElapsedMilliseconds;
            semaphore.Release();
        }
    }

    private async Task<List<ParallelNode>> GenerateBranchesAsync(
        ParallelNode node,
        ParallelGraphConfig cfg)
    {
        var decompositionPrompt = $"""
            Decompose the following sub-task into {Math.Min(cfg.MaxBranchesPerNode, 3)} parallel sub-questions.
            Each sub-question should explore a DIFFERENT aspect or angle independently.

            Sub-task: {node.Task}
            Result so far: {node.Result?[..Math.Min(300, node.Result!.Length)] ?? "N/A"}

            Output format (ONE per line):
            BRANCH: <sub-question>
            """;

        try
        {
            var response = await _chatClient.GetResponseAsync(decompositionPrompt);
            var text = response.Text ?? "";

            var branches = new List<ParallelNode>();
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("BRANCH:", StringComparison.OrdinalIgnoreCase))
                    continue;

                var subTask = trimmed.Replace("BRANCH:", "", StringComparison.OrdinalIgnoreCase)
                                     .Replace("branch:", "", StringComparison.OrdinalIgnoreCase)
                                     .Trim();

                if (string.IsNullOrEmpty(subTask) || subTask.Length < 5)
                    continue;

                if (branches.Count >= cfg.MaxBranchesPerNode)
                    break;

                var child = new ParallelNode
                {
                    Task = subTask,
                    Parent = node,
                    Depth = node.Depth + 1,
                    ParentIds = new() { node.Id }
                };
                node.Children.Add(child);
                _allNodes[child.Id] = child;
                branches.Add(child);
            }

            return branches;
        }
        catch
        {
            return new List<ParallelNode>();
        }
    }

    private async Task<string> MergeResultsAsync(
        ParallelNode root,
        string originalTask,
        ParallelGraphConfig cfg,
        CancellationToken cancellationToken)
    {
        var allResults = _allNodes.Values
            .Where(n => n.State == NodeState.Completed && n.Result != null)
            .OrderByDescending(n => n.Confidence)
            .ToList();

        if (allResults.Count <= 1)
            return root.Result ?? "";

        var contextParts = allResults.Take(10).Select(n =>
            $"[Node {n.Id} depth={n.Depth} conf={n.Confidence:F2}]: {n.Result?[..Math.Min(400, n.Result!.Length)]}");

        var mergePrompt = $"""
            Original task: {originalTask}

            {allResults.Count} parallel branches explored this task. Synthesize a unified answer:

            {string.Join("\n\n", contextParts)}

            Provide a comprehensive answer that integrates the best insights from all branches.
            """;

        try
        {
            var response = await _chatClient.GetResponseAsync(mergePrompt, cancellationToken: cancellationToken);
            return response.Text ?? root.Result ?? "";
        }
        catch
        {
            return root.Result ?? "";
        }
    }

    private static string BuildNodePrompt(string task, string parentContext, int depth)
    {
        var parts = new List<string>();
        parts.Add($"## Reasoning Task (Depth {depth})");
        parts.Add(task);

        if (!string.IsNullOrEmpty(parentContext))
        {
            parts.Add("## Context from Parent");
            parts.Add(parentContext);
        }

        parts.Add("Provide a concise answer with supporting evidence.");
        return string.Join("\n\n", parts);
    }

    private static double EstimateConfidence(string result)
    {
        if (string.IsNullOrEmpty(result)) return 0;
        double score = 0.5;

        if (result.Length > 100) score += 0.1;
        if (result.Length > 300) score += 0.1;

        var confidenceMarkers = new[] { "confident", "clearly", "certainly", "确认", "明确", "显然",
            "therefore", "thus", "hence", "因此", "所以", "综上所述" };
        foreach (var marker in confidenceMarkers)
            if (result.Contains(marker, StringComparison.OrdinalIgnoreCase))
                score += 0.03;

        var uncertaintyMarkers = new[] { "unsure", "unclear", "might", "maybe", "perhaps",
            "不确定", "可能", "也许", "或许", "probably" };
        foreach (var marker in uncertaintyMarkers)
            if (result.Contains(marker, StringComparison.OrdinalIgnoreCase))
                score -= 0.05;

        return Math.Max(0.1, Math.Min(0.99, score));
    }
}
