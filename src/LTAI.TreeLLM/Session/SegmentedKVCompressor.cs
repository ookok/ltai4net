using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.Core.System;
using LTAI.TreeLLM.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.TreeLLM.Session;

public sealed class SegmentedKVCompressor
{
    private readonly ILogger<SegmentedKVCompressor>? _logger;
    private readonly EntropyScheduler? _scheduler;

    private const int SEGMENT_SIZE = 8;
    private const int KV_TAIL_TOKENS = 500;
    private const int TRUNCATED_K = 1;

    public SegmentedKVCompressor(ILogger<SegmentedKVCompressor>? logger = null, EntropyScheduler? scheduler = null)
    {
        _logger = logger;
        _scheduler = scheduler;
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
                var prevSegment = segments[i - 1];
                var nextSegment = segments[i];
                var transitionEntropy = ComputeTransitionEntropy(prevSegment, nextSegment);

                if (transitionEntropy > 0.4)
                {
                    var tail = BuildKVTail(prevSegment, chatFn);
                    result.Add(new Dictionary<string, string>
                    {
                        ["role"] = "system",
                        ["content"] = $"[KV-Tail: {tail.Text}]"
                    });

                    if (transitionEntropy > 0.65)
                    {
                        var scratchpad = BuildScratchpad(prevSegment, nextSegment, chatFn);
                        if (!string.IsNullOrWhiteSpace(scratchpad))
                        {
                            result.Add(new Dictionary<string, string>
                            {
                                ["role"] = "system",
                                ["content"] = $"[Scratchpad: {scratchpad}]"
                            });
                        }
                    }
                }
                else
                {
                    var tail = BuildKVTail(prevSegment, chatFn);
                    result.Add(new Dictionary<string, string>
                    {
                        ["role"] = "system",
                        ["content"] = $"[KV-Tail: {tail.Text}]"
                    });
                }
            }

            result.AddRange(segments[i]);
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
        var globalEntropy = EstimateEntropy(messages);

        int baseSegSize = globalEntropy switch
        {
            > 0.7 => 3,
            > 0.5 => 5,
            > 0.3 => SEGMENT_SIZE,
            _ => 14
        };

        if (_scheduler != null)
        {
            double schedulerEntropy = _scheduler.CurrentEntropy;
            baseSegSize = (int)(baseSegSize * (1.0 + schedulerEntropy * 0.5));
            baseSegSize = Math.Max(3, Math.Min(20, baseSegSize));
        }

        var i = 0;
        while (i < messages.Count)
        {
            var remaining = messages.Count - i;
            var segSize = Math.Min(baseSegSize, remaining);

            if (i + segSize < messages.Count)
            {
                var localEntropy = EstimateLocalEntropy(messages, i, segSize);
                segSize = localEntropy switch
                {
                    > 0.6 => Math.Max(3, segSize / 2),
                    > 0.3 => segSize,
                    _ => Math.Min(20, segSize * 2)
                };
            }

            segSize = Math.Min(segSize, messages.Count - i);
            segments.Add(messages.GetRange(i, segSize));
            i += segSize;
        }

        return segments;
    }

    private double EstimateLocalEntropy(List<Dictionary<string, string>> messages, int start, int length)
    {
        var end = Math.Min(start + length, messages.Count);
        var slice = messages.Skip(start).Take(end - start).ToList();
        return EstimateEntropy(slice);
    }

    private double EstimateEntropy(List<Dictionary<string, string>> messages)
    {
        var markers = new[] { "tool_call", "code", "```", "error", "Error", "Exception",
            "decided", "approach", "决定", "方案", "选择", "def ", "class ", "function" };
        int count = 0;
        var uniqueTokens = new HashSet<string>();
        var totalLength = 0;
        var roleTransitions = 0;
        string? prevRole = null;

        for (int i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.TryGetValue("content", out var c))
            {
                foreach (var marker in markers)
                    if (c.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    { count++; break; }

                foreach (var word in c.Split(' ', '\n', '\r'))
                {
                    if (word.Length >= 2)
                        uniqueTokens.Add(word.ToLowerInvariant());
                }
                totalLength += c.Length;
            }

            if (m.TryGetValue("role", out var r))
            {
                if (prevRole != null && r != prevRole) roleTransitions++;
                prevRole = r;
            }
        }

        double keywordDensity = Math.Min(1.0, (double)count / Math.Max(1, messages.Count) * 2);
        double vocabRichness = messages.Count > 0
            ? Math.Min(1.0, (double)uniqueTokens.Count / Math.Max(1, totalLength / 4) * 3)
            : 0;
        double transitionRate = messages.Count > 1
            ? (double)roleTransitions / (messages.Count - 1)
            : 0;

        return keywordDensity * 0.4 + vocabRichness * 0.35 + transitionRate * 0.25;
    }

    private double ComputeTransitionEntropy(
        List<Dictionary<string, string>> prev, List<Dictionary<string, string>> next)
    {
        if (prev.Count == 0 || next.Count == 0) return 0.5;

        var prevLast = prev.Last();
        var nextFirst = next.First();
        var prevContent = prevLast.TryGetValue("content", out var pc) ? pc : "";
        var nextContent = nextFirst.TryGetValue("content", out var nc) ? nc : "";

        var prevWords = new HashSet<string>(prevContent.Split(' ', '\n', '\r')
            .Where(w => w.Length >= 2).Select(w => w.ToLowerInvariant()));
        var nextWords = new HashSet<string>(nextContent.Split(' ', '\n', '\r')
            .Where(w => w.Length >= 2).Select(w => w.ToLowerInvariant()));

        if (prevWords.Count == 0 || nextWords.Count == 0) return 0.5;

        var overlap = prevWords.Intersect(nextWords).Count();
        var jaccard = (double)overlap / (prevWords.Count + nextWords.Count - overlap);

        var prevRole = prevLast.TryGetValue("role", out var pr) ? pr : "";
        var nextRole = nextFirst.TryGetValue("role", out var nr) ? nr : "";
        var roleChange = prevRole != nextRole ? 0.3 : 0;

        double sizeRatio = Math.Min(1.0, (double)nextContent.Length / Math.Max(1, prevContent.Length));

        return (1.0 - jaccard) * 0.5 + roleChange * 0.3 + Math.Abs(sizeRatio - 0.5) * 0.2;
    }

    private string? BuildScratchpad(
        List<Dictionary<string, string>> prevSegment,
        List<Dictionary<string, string>> nextSegment,
        Func<string, string>? chatFn)
    {
        if (chatFn == null) return null;

        var lastMessages = prevSegment.TakeLast(Math.Min(2, prevSegment.Count)).ToList();
        var firstMessages = nextSegment.Take(Math.Min(2, nextSegment.Count)).ToList();

        var context = string.Join("\n", lastMessages.Select(m =>
            m.TryGetValue("content", out var c) ? c[..Math.Min(c.Length, 200)] : ""));
        var upcoming = string.Join("\n", firstMessages.Select(m =>
            m.TryGetValue("content", out var c) ? c[..Math.Min(c.Length, 200)] : ""));

        try
        {
            var prompt = $"Bridge context: Previous segment discusses: {context}\n\n" +
                         $"Upcoming segment begins with: {upcoming}\n\n" +
                         "In one short sentence, state the key transition or carry-forward insight connecting these.";
            var scratchpad = chatFn(prompt);
            return scratchpad.Length > 300 ? scratchpad[..300] : scratchpad;
        }
        catch
        {
            return null;
        }
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
