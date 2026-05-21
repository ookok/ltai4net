using System.Text.RegularExpressions;
using System.Threading;
using LTAI.Core.Utility;

namespace LTAI.TreeLLM.Session;

public sealed class SessionCompressor
{
    public record ChatMessage(string Role, string Content);

    private static readonly Regex DecisionRegex = new(
        @"决定|结论|方案|decided|decision|conclusion|plan|will\s+do|should\s+use|最终|确定|选定",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int RecentTurns = 5;
    private const int MiddleTurns = 10;

    private int _compressionCount;

    private Func<string, string, Task<string>>? _chatFn;

    public void SetChatFunction(Func<string, string, Task<string>> chatFn)
    {
        _chatFn = chatFn;
    }

    public async Task<List<ChatMessage>> Compress(
        List<ChatMessage> messages, int maxTokens, Func<string, string, Task<string>>? chatFn = null)
    {
        if (messages == null || messages.Count == 0)
            return new List<ChatMessage>();

        var fn = chatFn ?? _chatFn;
        var result = new List<ChatMessage>();
        var total = messages.Count;

        for (var i = Math.Max(0, total - RecentTurns); i < total; i++)
            result.Add(messages[i]);

        var middleStart = Math.Max(0, total - RecentTurns - MiddleTurns);
        var middleEnd = Math.Max(0, total - RecentTurns);
        var middle = messages.Skip(middleStart).Take(middleEnd - middleStart).ToList();

        if (middle.Count > 0 && fn != null)
        {
            var summary = await _Summarize(middle, fn);
            result.Insert(0, new ChatMessage("system", summary));
        }
        else if (middle.Count > 3 && fn == null)
        {
            var fallback = middle.Skip(middle.Count - 3).ToList();
            foreach (var msg in fallback)
                result.Insert(0, msg);
        }
        else if (middle.Count > 0)
        {
            foreach (var msg in middle)
                result.Insert(0, msg);
        }

        var oldEnd = middleStart;
        if (oldEnd > 0)
        {
            var old = messages.Take(oldEnd).ToList();
            var decisions = _ExtractDecisions(old);
            if (!string.IsNullOrEmpty(decisions))
                result.Insert(0, new ChatMessage("system", $"[历史决策摘要] {decisions}"));
        }

        var totalChars = result.Sum(m => m.Content.Length);
        var charLimit = maxTokens * 4;
        while (totalChars > charLimit && result.Count > RecentTurns + 1)
        {
            var firstNonRecent = result.FindIndex(m => m.Role != "system" || !m.Content.StartsWith("["));
            if (firstNonRecent < 0) break;
            totalChars -= result[firstNonRecent].Content.Length;
            result.RemoveAt(firstNonRecent);
        }

        Interlocked.Increment(ref _compressionCount);
        return result;
    }

    public async Task<List<ChatMessage>> WeightedCompress(
        List<ChatMessage> messages, int maxTokens,
        Func<string, string, Task<string>> chatFn, string query)
    {
        if (messages == null || messages.Count == 0)
            return new List<ChatMessage>();

        var fn = chatFn ?? _chatFn;
        var result = new List<ChatMessage>();
        var n = messages.Count;
        var queryTokens = TextUtility.Tokenize(query);

        for (var i = Math.Max(0, n - RecentTurns); i < n; i++)
        {
            result.Add(messages[i]);
        }

        var totalScoreSum = 0.0;
        var candidateWindow = messages.Take(Math.Max(0, n - RecentTurns)).ToList();
        var scored = new List<(ChatMessage msg, int origIdx, double crit)>();

        for (var idx = 0; idx < candidateWindow.Count; idx++)
        {
            var msg = candidateWindow[idx];
            var crit = ComputeCriticality(msg, idx, n, queryTokens);
            totalScoreSum += crit;
            scored.Add((msg, idx, crit));
        }

        scored = scored.OrderByDescending(x => x.crit).ToList();

        foreach (var (msg, _, crit) in scored)
        {
            if (crit > 0.5)
            {
                result.Insert(0, msg);
            }
            else if (crit > 0.25 && fn != null)
            {
                var snippet = await _SingleMessageSummarize(msg, fn);
                result.Insert(0, new ChatMessage("system", snippet));
            }
            else if (crit > 0.15)
            {
                var snippet = TextUtility.TruncateSnippet(msg.Content, 80);
                result.Insert(0, new ChatMessage(msg.Role, snippet));
            }
        }

        var totalChars = result.Sum(m => m.Content.Length);
        var charLimit = maxTokens * 4;
        for (var i = 0; i < result.Count - RecentTurns && totalChars > charLimit; i++)
        {
            totalChars -= result[i].Content.Length;
            result.RemoveAt(i);
            i--;
        }

        Interlocked.Increment(ref _compressionCount);
        return result;
    }

    private async Task<string> _Summarize(
        List<ChatMessage> messages, Func<string, string, Task<string>> chatFn)
    {
        var conversation = string.Join("\n", messages.TakeLast(10).Select(m => $"[{m.Role}]: {m.Content}"));
        var prompt = $"用中文将以下对话压缩为不超过200字的摘要，保留关键决策和结论：\n\n{conversation}";

        try
        {
            return await chatFn("system", prompt);
        }
        catch
        {
            return TextUtility.TruncateSnippet(string.Join(" | ", messages.TakeLast(3).Select(m => m.Content)), 200);
        }
    }

    private async Task<string> _SingleMessageSummarize(
        ChatMessage message, Func<string, string, Task<string>> chatFn)
    {
        var prompt = $"用中文将以下内容压缩为一句话摘要（不超过60字）：\n\n{message.Content}";

        try
        {
            return await chatFn("system", prompt);
        }
        catch
        {
            return TextUtility.TruncateSnippet(message.Content, 60);
        }
    }

    private string _ExtractDecisions(List<ChatMessage> messages)
    {
        var decisions = new List<string>();
        foreach (var msg in messages)
        {
            var matches = DecisionRegex.Matches(msg.Content);
            if (matches.Count == 0) continue;

            foreach (Match match in matches)
            {
                var start = Math.Max(0, match.Index - 15);
                var end = Math.Min(msg.Content.Length, match.Index + match.Length + 30);
                var snippet = msg.Content[start..end].Replace('\n', ' ').Trim();
                if (snippet.Length > 10 && !decisions.Contains(snippet))
                    decisions.Add(snippet);
            }

            if (decisions.Count >= 5)
                break;
        }

        return string.Join("; ", decisions.Take(5));
    }

    public double _NtkDecayScale(int n, int dim = 8)
    {
        if (n <= 20)
            return 1.0;

        var alpha = Math.Pow(n / 20.0, 1.0 / (dim - 2));
        return 1.0 / alpha;
    }

    private static double ComputeCriticality(
        ChatMessage msg, int idx, int total, HashSet<string> queryTokens)
    {
        var msgTokens = TextUtility.Tokenize(msg.Content);

        var queryOverlap = queryTokens.Count > 0 && msgTokens.Count > 0
            ? TextUtility.JaccardSimilarity(queryTokens, msgTokens)
            : 0.0;

        var decisionScore = DecisionRegex.IsMatch(msg.Content) ? 1.0 : 0.0;

        var roleScore = msg.Role switch
        {
            "assistant" => 0.3,
            "user" => 0.2,
            _ => 0.1
        };

        var positionDecay = 1.0 - ((double)(total - idx - 1) / total);
        var ntky = 1.0 / Math.Max(1.0, idx * 0.05 + 1.0);

        var normImportance = Math.Min(1.0, msg.Content.Length / 200.0);

        return queryOverlap * 0.30
             + decisionScore * 0.25
             + roleScore * 0.10
             + positionDecay * 0.20
             + normImportance * 0.15;
    }
}
