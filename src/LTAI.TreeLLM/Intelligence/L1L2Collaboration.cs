using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LTAI.TreeLLM.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.TreeLLM.Intelligence;

public sealed partial class L1L2Collaboration
{
    private static readonly Lazy<L1L2Collaboration> _instanceLazy = new(() => new L1L2Collaboration());
    public static L1L2Collaboration Instance => _instanceLazy.Value;

    [GeneratedRegex(@"<need\s+id=""([^""]*)""\s+type=""([^""]*)""\s+level=""([^""]*)""(?:\s+timeout=""([^""]*)"")?\s*>(.*?)</need>",
        RegexOptions.Singleline)]
    private static partial Regex NeedRegex();

    private readonly ConcurrentDictionary<string, string> _worldState = new();
    private readonly ConcurrentDictionary<string, List<Need>> _sessionNeeds = new();
    private Func<string, int, Task<string?>>? _humanCallback;
    private readonly List<object> _history = new();
    private string _l2Feedback = "";
    private readonly ILogger<L1L2Collaboration>? _logger;

    public L1L2Collaboration(ILogger<L1L2Collaboration>? logger = null)
    {
        _logger = logger;
    }

    public async Task<CollaborationResult> CollaborativeChat(
        string userQuery,
        int maxRounds,
        Func<string, int, Task<string?>>? humanCallback,
        string l2Provider,
        string l2Model,
        Func<string, string, Task<string>> chatFn,
        Func<string, string, Task<string>>? l1ChatFn = null,
        string extraContext = "")
    {
        _humanCallback = humanCallback;
        var needs = new List<Need>();
        var insights = new List<string>();
        var totalTokens = 0;
        var totalLatency = 0.0;
        var fullText = new List<string>();

        var l1Preload = await L1Preload(userQuery, l1ChatFn);

        var systemPrompt = BuildSystemPrompt(l2Provider, l2Model, l1Preload, extraContext);
        var context = $"### User Query\n{userQuery}\n\n{extraContext}\n\n### Previous Feedback\n{_l2Feedback}";

        for (var round = 0; round < maxRounds; round++)
        {
            var roundStart = DateTime.UtcNow;

            var l2Response = await _L2Reason(context, new List<string> { systemPrompt }, chatFn);
            totalTokens += EstimateTokens(l2Response);

            var roundNeeds = ParseNeeds(l2Response);
            if (roundNeeds.Count == 0)
            {
                fullText.Add(l2Response);
                insights.Add("L2 resolved without needs");
                break;
            }

            var l2Text = NeedRegex().Replace(l2Response, "").Trim();
            if (!string.IsNullOrWhiteSpace(l2Text))
                fullText.Add(l2Text);

            var fulfillTasks = new Dictionary<string, Task<string?>>();
            foreach (var need in roundNeeds)
            {
                needs.Add(need);
                if (need.Level == DelegateLevel.FireAndForget)
                {
                    _ = _L1Fulfill(need, l1ChatFn);
                }
                else
                {
                    fulfillTasks[need.Id] = FulfillAndTrack(need, l1ChatFn);
                }
            }

            await Task.WhenAll(fulfillTasks.Values);

            var needResults = new List<string>();
            foreach (var (needId, task) in fulfillTasks)
            {
                var result = await task;
                if (result != null)
                    needResults.Add($"<need_result id=\"{needId}\">\n{result}\n</need_result>");
            }

            if (needResults.Count > 0)
                context += $"\n\n### Need Results (Round {round + 1})\n{string.Join("\n", needResults)}";

            var roundNeedsApproval = roundNeeds.Where(n => n.Level == DelegateLevel.NeedApproval).ToList();
            if (roundNeedsApproval.Count > 0)
            {
                context += "\n\n### Needs Requiring Approval\n" +
                    string.Join("\n", roundNeedsApproval.Select(n =>
                        $"<need id=\"{n.Id}\" type=\"{n.Type}\" level=\"{n.Level}\">{n.Description}</need>"));
            }

            totalLatency += (DateTime.UtcNow - roundStart).TotalMilliseconds;

            if (round == maxRounds - 1)
                insights.Add($"Max rounds ({maxRounds}) reached");
        }

        return new CollaborationResult
        {
            Text = string.Join("\n\n", fullText),
            Needs = needs,
            Rounds = Math.Min(maxRounds, needs.Count > 0 ? maxRounds : 1),
            TotalTokens = totalTokens,
            TotalLatencyMs = totalLatency,
            Insights = insights
        };
    }

    public async Task<Dictionary<string, object>> Conference(
        string problem,
        Dictionary<string, string> context,
        int maxRounds,
        Func<string, string, Task<string>> chatFn)
    {
        var l1Proposals = new List<string>();
        var deliberation = new List<string>();

        for (var round = 0; round < maxRounds; round++)
        {
            var ctx = $"Problem: {problem}\n\nContext:\n{string.Join("\n", context.Select(kv => $"{kv.Key}: {kv.Value}"))}\n\nPrevious proposals:\n{string.Join("\n", l1Proposals)}\n\nL2 evaluations:\n{string.Join("\n", deliberation)}";

            var l1Response = await chatFn(
                $"You are L1 (fast model). Propose a quick fix or partial solution. Be concise.\n\n{ctx}",
                "l1");
            l1Proposals.Add(l1Response);

            var l2Response = await chatFn(
                $"You are L2 (deep reasoning). Evaluate the L1 proposal and suggest improvements or alternatives. Be precise.\n\nProposal: {l1Response}\n\n{ctx}",
                "l2");
            deliberation.Add(l2Response);
        }

        var final = await chatFn(
            $"Synthesize a final JSON decision with keys: decision (string), confidence (0-1), rationale (string).\n\nProblem: {problem}\n\nProposals:\n{string.Join("\n---\n", l1Proposals)}\n\nEvaluations:\n{string.Join("\n---\n", deliberation)}",
            "l2");

        return new Dictionary<string, object>
        {
            ["decision"] = final,
            ["proposals"] = l1Proposals,
            ["evaluations"] = deliberation,
            ["rounds"] = maxRounds
        };
    }

    private async Task<string> _L2Reason(string context, List<string> knowledge, Func<string, string, Task<string>> chatFn)
    {
        return await chatFn(context, "l2");
    }

    private async Task<string?> FulfillAndTrack(Need need, Func<string, string, Task<string>>? l1ChatFn)
    {
        try
        {
            var result = await _L1Fulfill(need, l1ChatFn);
            need.Fulfilled = true;
            need.Result = result;
            return result;
        }
        catch (Exception ex)
        {
            need.Error = ex.Message;
            _logger?.LogWarning(ex, "Failed to fulfill need {NeedId}", need.Id);
            return null;
        }
    }

    private async Task<string> _L1Fulfill(Need need, Func<string, string, Task<string>>? l1ChatFn)
    {
        switch (need.Type)
        {
            case NeedType.File:
                return await HandleFileNeed(need);

            case NeedType.Tool:
                return await HandleToolNeed(need, l1ChatFn);

            case NeedType.Knowledge:
            case NeedType.Search:
                return await HandleKnowledgeNeed(need, l1ChatFn);

            case NeedType.Sql:
                return await HandleSqlNeed(need, l1ChatFn);

            case NeedType.Question:
                if (l1ChatFn != null)
                    return await l1ChatFn(need.Description, "l1");
                return "L1 chat function not available";

            case NeedType.Human:
                return await _AskHuman(need.Description, need.Params.TryGetValue("timeout", out var t) && int.TryParse(t, out var timeout) ? timeout : 30, _humanCallback)
                    ?? "No human response";

            default:
                if (l1ChatFn != null)
                    return await l1ChatFn(need.Description, "l1");
                return "Unable to fulfill: no L1 chat function available";
        }
    }

    private static Task<string> HandleFileNeed(Need need)
    {
        var result = "File operation placeholder";
        if (need.Params.TryGetValue("path", out var path))
            result = $"Would read file: {path}";
        if (need.Params.TryGetValue("content", out var content))
            result = $"Would write to file with content length: {content.Length}";
        return Task.FromResult(result);
    }

    private static Task<string> HandleToolNeed(Need need, Func<string, string, Task<string>>? l1ChatFn)
    {
        var description = need.Description;
        if (need.Params.TryGetValue("tool_name", out var toolName))
            description = $"Tool: {toolName}. {description}";
        return Task.FromResult($"Echo tool need: {description}");
    }

    private static async Task<string> HandleKnowledgeNeed(Need need, Func<string, string, Task<string>>? l1ChatFn)
    {
        if (need.Params.TryGetValue("query", out var query))
        {
            if (l1ChatFn != null)
                return await l1ChatFn($"Knowledge retrieval: {query}", "l1");
            return $"Vector search placeholder for: {query}";
        }
        return "Knowledge need: no query specified";
    }

    private static Task<string> HandleSqlNeed(Need need, Func<string, string, Task<string>>? l1ChatFn)
    {
        return Task.FromResult($"Echo SQL need: {need.Description}");
    }

    private async Task<string?> _AskHuman(string question, int timeout, Func<string, int, Task<string?>>? callback)
    {
        if (callback == null) return null;

        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
            return await callback(question, timeout).WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Human callback timed out after {Timeout}s for: {Question}", timeout, question);
            return null;
        }
    }

    private async Task<List<string>> L1Preload(string userQuery, Func<string, string, Task<string>>? l1ChatFn)
    {
        var preload = new List<string>();
        if (l1ChatFn == null) return preload;

        try
        {
            var context = await l1ChatFn(
                $"Given the user query: \"{userQuery}\", what context, knowledge, or tools should L2 have ready? Answer with a brief bullet list.",
                "l1");
            preload.AddRange(context.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "L1 preload failed");
        }

        return preload;
    }

    private string BuildSystemPrompt(string l2Provider, string l2Model, List<string> l1Preload, string extraContext)
    {
        var preloadText = l1Preload.Count > 0
            ? $"\n\n### Preloaded Context (L1)\n{string.Join("\n", l1Preload.Select(p => $"- {p}"))}"
            : "";

        return $"""
You are an AI assistant with a delegation protocol. You can create <need> tags to delegate tasks to sub-agents.

Available need types: Tool, Knowledge, File, Sql, Search, Human, Question
Available levels: FireAndForget, NeedResult, NeedApproval

Example:
<need id="1" type="Tool" level="NeedResult">Run a web search for latest docs</need>
<need id="2" type="File" level="FireAndForget">Save the output to results.txt</need>

When you have no more needs, provide your final answer without need tags.

Provider: {l2Provider}
Model: {l2Model}
{preloadText}
""";
    }

    private static List<Need> ParseNeeds(string text)
    {
        var needs = new List<Need>();
        var matches = NeedRegex().Matches(text);

        foreach (Match match in matches)
        {
            var id = match.Groups[1].Value;
            var typeStr = match.Groups[2].Value;
            var levelStr = match.Groups[3].Value;
            var description = match.Groups[5].Value.Trim();

            if (!Enum.TryParse<NeedType>(typeStr, ignoreCase: true, out var type))
                type = NeedType.Question;

            if (!Enum.TryParse<DelegateLevel>(levelStr, ignoreCase: true, out var level))
                level = DelegateLevel.NeedResult;

            needs.Add(new Need
            {
                Id = id,
                Type = type,
                Level = level,
                Description = description,
                CreatedAt = DateTime.UtcNow
            });
        }

        return needs;
    }

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var cnChars = text.Count(c => c >= 0x4e00 && c <= 0x9fff);
        var enWords = (text.Length - cnChars) / 4;
        return cnChars + Math.Max(1, enWords);
    }
}
