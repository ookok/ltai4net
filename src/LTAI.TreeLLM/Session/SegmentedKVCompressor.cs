using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using LTAI.TreeLLM.Models;

namespace LTAI.TreeLLM.Session;

public sealed class SegmentedKVCompressor
{
    private readonly ILogger<SegmentedKVCompressor>? _logger;

    private const int SEGMENT_SIZE = 8;
    private const int KV_TAIL_TOKENS = 500;
    private const int TRUNCATED_K = 1;

    public SegmentedKVCompressor(ILogger<SegmentedKVCompressor>? logger = null)
    {
        _logger = logger;
    }

    public List<Dictionary<string, string>> Compress(
        List<Dictionary<string, string>> messages, int maxTokens, Func<string, string>? chatFn = null)
    {
        if (messages.Count <= SEGMENT_SIZE * 2)
            return messages;

        var segments = SplitSegments(messages);
        var result = new List<Dictionary<string, string>>();

        for (int i = 0; i < segments.Count; i++)
        {
            if (i > 0)
            {
                var tail = BuildKVTail(segments[i - 1], chatFn);
                result.Add(new Dictionary<string, string>
                {
                    ["role"] = "system",
                    ["content"] = $"[KV-Tail: {tail.Text}]"
                });
            }

            var segment = segments[i];
            result.AddRange(segment);
        }

        if (EstimateTokens(result) > maxTokens)
            result = TruncateToBudget(result, maxTokens);

        return result;
    }

    public List<Dictionary<string, string>> WeightedCompress(
        List<Dictionary<string, string>> messages, string query, int maxTokens,
        Func<string, string>? chatFn = null)
    {
        if (messages.Count <= 5) return messages;

        var scored = messages.Select((m, i) =>
        {
            var content = m.TryGetValue("content", out var c) ? c : "";
            var role = m.TryGetValue("role", out var r) ? r : "";
            var crit = ScoreCriticality(content, query, role, i, messages.Count);
            return (Message: m, Criticality: crit, Index: i);
        }).ToList();

        var result = new List<Dictionary<string, string>>();

        foreach (var item in scored)
        {
            if (item.Criticality > 0.5)
            {
                result.Add(item.Message);
            }
            else if (item.Criticality > 0.25 && chatFn != null)
            {
                var content = item.Message.TryGetValue("content", out var c) ? c : "";
                var summary = chatFn($"Summarize in one sentence: {content}");
                result.Add(new Dictionary<string, string>
                {
                    ["role"] = item.Message.TryGetValue("role", out var r) ? r : "user",
                    ["content"] = summary.Length > 200 ? summary[..200] : summary
                });
            }
        }

        if (EstimateTokens(result) > maxTokens)
            result = TruncateToBudget(result, maxTokens);

        return result;
    }

    public Dictionary<string, object> ExtractTail(List<Dictionary<string, string>> messages)
    {
        var tail = BuildKVTail(messages, null);
        return new Dictionary<string, object>
        {
            ["text"] = tail.Text,
            ["hash"] = tail.Hash,
            ["decision_signatures"] = tail.DecisionSignatures,
            ["token_count"] = tail.TokenCount
        };
    }

    public List<Dictionary<string, string>> RestoreTail(Dictionary<string, object> tailState)
    {
        var text = tailState.TryGetValue("text", out var t) ? t?.ToString() ?? "" : "";
        return text.Length > 0
            ? new List<Dictionary<string, string>>
            {
                new() { ["role"] = "system", ["content"] = $"[Session state: {text}]" }
            }
            : new List<Dictionary<string, string>>();
    }

    private List<List<Dictionary<string, string>>> SplitSegments(
        List<Dictionary<string, string>> messages)
    {
        var segments = new List<List<Dictionary<string, string>>>();
        var entropy = EstimateEntropy(messages);
        var segSize = entropy switch
        {
            > 0.7 => 4,
            > 0.3 => SEGMENT_SIZE,
            _ => 16
        };

        for (int i = 0; i < messages.Count; i += segSize)
            segments.Add(messages.GetRange(i, Math.Min(segSize, messages.Count - i)));

        return segments;
    }

    private double EstimateEntropy(List<Dictionary<string, string>> messages)
    {
        var markers = new[] { "tool_call", "code", "```", "error", "Error", "Exception",
            "decided", "approach", "决定", "方案", "选择", "def ", "class ", "function" };
        int count = 0;

        foreach (var m in messages)
        {
            if (m.TryGetValue("content", out var c))
            {
                foreach (var marker in markers)
                    if (c.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    { count++; break; }
            }
        }

        return Math.Min(1.0, (double)count / messages.Count * 2);
    }

    private KVTail BuildKVTail(List<Dictionary<string, string>> segment, Func<string, string>? chatFn)
    {
        var decisions = ExtractDecisions(segment);
        var lastMessages = segment.TakeLast(Math.Min(3, segment.Count)).ToList();
        var tailText = string.Join(" | ", lastMessages.Select(m =>
            m.TryGetValue("content", out var c) ? c : ""));

        if (chatFn != null)
        {
            try
            {
                tailText = chatFn($"Summarize this conversation segment in {KV_TAIL_TOKENS / 4} words: {tailText}");
            }
            catch { }
        }

        if (tailText.Length > KV_TAIL_TOKENS * 4)
            tailText = tailText[..(KV_TAIL_TOKENS * 4)];

        return new KVTail
        {
            SourceSegmentId = Guid.NewGuid().ToString("N")[..8],
            Text = tailText,
            Hash = ComputeHash(tailText),
            DecisionSignatures = decisions,
            TokenCount = tailText.Length / 4
        };
    }

    private static List<string> ExtractDecisions(List<Dictionary<string, string>> messages)
    {
        var keywords = new[] { "decided", "will use", "plan is", "using", "choose", "approach",
            "决定", "使用", "方案", "选择", "采用", "确定" };
        var decisions = new List<string>();

        foreach (var msg in messages)
        {
            if (msg.TryGetValue("content", out var c))
            {
                foreach (var kw in keywords)
                {
                    if (c.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    {
                        decisions.Add(c.Length > 120 ? c[..120] : c);
                        break;
                    }
                }
            }
        }

        return decisions;
    }

    private static double ScoreCriticality(string content, string query, string role, int index, int totalCount)
    {
        double score = 0;

        var queryWords = new HashSet<string>(query.ToLower()
            .Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
        var contentWords = new HashSet<string>(content.ToLower()
            .Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));

        if (queryWords.Count > 0 && contentWords.Count > 0)
        {
            var overlap = queryWords.Intersect(contentWords).Count();
            var jaccard = (double)overlap / (queryWords.Count + contentWords.Count - overlap);
            score += jaccard * 0.30;
        }

        var decisionKeywords = new[] { "decided", "will use", "plan is", "using", "choose", "approach",
            "决定", "使用", "方案", "选择", "采用", "确定" };
        if (decisionKeywords.Any(k => content.Contains(k, StringComparison.OrdinalIgnoreCase)))
            score += 0.25;

        if (role == "assistant" || role == "system")
            score += 0.10;

        var positionWeight = 1.0 - (double)index / totalCount;
        score += positionWeight * 0.20;

        if (content.Length > 200)
            score += Math.Min(0.15, content.Length * 0.0001);

        return Math.Min(1.0, score);
    }

    private static int EstimateTokens(List<Dictionary<string, string>> messages)
    {
        return messages.Sum(m => m.TryGetValue("content", out var c) ? c.Length / 4 : 0);
    }

    private static List<Dictionary<string, string>> TruncateToBudget(
        List<Dictionary<string, string>> messages, int maxTokens)
    {
        var result = new List<Dictionary<string, string>>();
        int tokenCount = 0;

        foreach (var msg in messages.AsEnumerable().Reverse())
        {
            var content = msg.TryGetValue("content", out var c) ? c : "";
            var tokens = content.Length / 4;
            if (tokenCount + tokens <= maxTokens)
            {
                result.Insert(0, msg);
                tokenCount += tokens;
            }
        }

        return result;
    }

    private static string ComputeHash(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes)[..12];
    }
}
