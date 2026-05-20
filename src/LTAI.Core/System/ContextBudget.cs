namespace LTAI.Core.System;

public sealed class BudgetState
{
    public int TotalTokens { get; set; }
    public int SystemTokens { get; set; }
    public int HistoryTokens { get; set; }
    public int CurrentTokens { get; set; }
    public int MaxTokens { get; set; } = 50000;
    public int MaxTurns { get; set; } = 20;
    public int TurnCount { get; set; }
    public int CompressedTurns { get; set; }
    public double LastCompression { get; set; }
    public int CompressionCount { get; set; }
    public int SpliceGapTokens { get; set; }
    public double AvgTransitionEntropy { get; set; }
}

public sealed class ContextBudget
{
    private static readonly Lazy<ContextBudget> _instance = new(() => new ContextBudget());
    public static ContextBudget Instance => _instance.Value;

    private const double TokensPerCharEstimate = 0.25;
    private const int SpliceGuardTokens = 60;
    private readonly BudgetState _state = new();

    private ContextBudget() { }

    public int EstimateTokens(string text)
    {
        return Math.Max(1, (int)(text.Length * TokensPerCharEstimate));
    }

    public void Reset()
    {
        var budget = new BudgetState
        {
            MaxTokens = _state.MaxTokens,
            MaxTurns = _state.MaxTurns
        };
        CopyState(budget);
    }

    public void Configure(int maxTokens = 0, int maxTurns = 0)
    {
        if (maxTokens > 0) _state.MaxTokens = maxTokens;
        if (maxTurns > 0) _state.MaxTurns = maxTurns;
    }

    public Dictionary<string, object> GetStats()
    {
        var usagePct = Math.Round((double)_state.TotalTokens / Math.Max(1, _state.MaxTokens) * 100, 1);

        return new Dictionary<string, object>
        {
            ["total_tokens"] = _state.TotalTokens,
            ["max_tokens"] = _state.MaxTokens,
            ["usage_pct"] = usagePct,
            ["turn_count"] = _state.TurnCount,
            ["max_turns"] = _state.MaxTurns,
            ["compressed_turns"] = _state.CompressedTurns,
            ["compression_count"] = _state.CompressionCount,
            ["splice_gap_tokens"] = _state.SpliceGapTokens,
            ["avg_transition_entropy"] = _state.AvgTransitionEntropy,
            ["over_budget"] = usagePct > 90
        };
    }

    public (bool needsCompression, List<Dictionary<string, string>> compressedHistory, int dropped)
        AddAndCheck(string systemPrompt, List<Dictionary<string, string>> history, string currentMsg)
    {
        _state.TurnCount++;

        var sysTokens = EstimateTokens(systemPrompt);
        var histTokens = history.Sum(m => EstimateTokens(m.GetValueOrDefault("content", "")));
        var curTokens = EstimateTokens(currentMsg);

        _state.SystemTokens = sysTokens;
        _state.HistoryTokens = histTokens;
        _state.CurrentTokens = curTokens;
        _state.TotalTokens = sysTokens + histTokens + curTokens;

        if (_state.TotalTokens <= _state.MaxTokens && _state.TurnCount <= _state.MaxTurns)
            return (false, history, 0);

        return Compress(history, sysTokens, curTokens);
    }

    private (bool needsCompression, List<Dictionary<string, string>> compressedHistory, int dropped)
        Compress(List<Dictionary<string, string>> history, int sysTokens, int curTokens)
    {
        var available = (int)(_state.MaxTokens * 0.85) - sysTokens - curTokens;
        if (available <= 0)
            available = (int)(_state.MaxTokens * 0.3);

        var spliceGuardBudget = Math.Min(available / 10, SpliceGuardTokens * 2);
        available -= spliceGuardBudget;

        var result = new List<Dictionary<string, string>>(history);
        var dropped = 0;
        const int keepRecent = 4;
        var splicePoints = new List<int>();

        if (result.Count > _state.MaxTurns)
        {
            var excess = result.Count - _state.MaxTurns;
            result.RemoveRange(0, excess);
            dropped += excess;
            if (excess > 0 && result.Count > 0)
                splicePoints.Add(0);
        }

        var totalHistTokens = result.Sum(m => EstimateTokens(m.GetValueOrDefault("content", "")));
        if (totalHistTokens > available)
        {
            var used = 0;
            var summaries = new List<string>();
            var excessStart = 0;

            for (var i = 0; i < result.Count; i++)
            {
                var tokens = EstimateTokens(result[i].GetValueOrDefault("content", ""));
                if (used + tokens > available && i < result.Count - keepRecent)
                {
                    var content = result[i].GetValueOrDefault("content", "");
                    var role = result[i].GetValueOrDefault("role", "?");
                    summaries.Add(SummarizeMessage(role, content));
                    dropped++;
                    excessStart = i + 1;
                }
                else
                {
                    used += tokens;
                }
            }

            if (summaries.Count > 0)
            {
                var summaryText = "Earlier context (summarized):\n" + string.Join("\n", summaries);
                result = new List<Dictionary<string, string>>
                {
                    new() { ["role"] = "system", ["content"] = summaryText }
                };
                result.AddRange(history.Skip(excessStart));
                splicePoints.Add(1);
            }
        }

        var transitionEntropies = new List<double>();
        for (var i = 1; i < result.Count; i++)
        {
            var prevContent = result[i - 1].GetValueOrDefault("content", "");
            var nextContent = result[i].GetValueOrDefault("content", "");

            var prevWords = new HashSet<string>(prevContent.Split(' ', '\n', '\r')
                .Where(w => w.Length >= 2).Select(w => w.ToLowerInvariant()));
            var nextWords = new HashSet<string>(nextContent.Split(' ', '\n', '\r')
                .Where(w => w.Length >= 2).Select(w => w.ToLowerInvariant()));

            if (prevWords.Count > 0 && nextWords.Count > 0)
            {
                var overlap = prevWords.Intersect(nextWords).Count();
                var jaccard = (double)overlap / (prevWords.Count + nextWords.Count - overlap);
                var entropy = 1.0 - jaccard;
                transitionEntropies.Add(entropy);
                if (entropy > 0.6) splicePoints.Add(i);
            }
        }

        if (splicePoints.Count > 0 && spliceGuardBudget > 0)
        {
            var transitionsAdded = 0;
            foreach (var sp in splicePoints.Distinct().OrderBy(sp => sp))
            {
                if (transitionsAdded >= 3) break;
                if (sp >= result.Count) continue;

                var prevContent = sp > 0 ? result[sp - 1].GetValueOrDefault("content", "") : "";
                var nextContent = sp < result.Count ? result[sp].GetValueOrDefault("content", "") : "";

                if (string.IsNullOrWhiteSpace(prevContent) || string.IsNullOrWhiteSpace(nextContent))
                    continue;

                var transitionEntropy = sp > 0 ? ComputeTransitionEntropy(prevContent, nextContent) : 0.5;
                if (transitionEntropy < 0.5) continue;

                var bridge = BuildSpliceGuard(prevContent, nextContent);
                if (!string.IsNullOrWhiteSpace(bridge))
                {
                    var guardTokens = EstimateTokens(bridge);
                    if (guardTokens <= spliceGuardBudget)
                    {
                        result.Insert(sp, new Dictionary<string, string>
                        {
                            ["role"] = "system",
                            ["content"] = $"[Transition: {bridge}]"
                        });
                        spliceGuardBudget -= guardTokens;
                        transitionsAdded++;
                        _state.SpliceGapTokens += guardTokens;
                    }
                }
            }
        }

        _state.AvgTransitionEntropy = transitionEntropies.Count > 0
            ? transitionEntropies.Average() : 0;
        _state.CompressedTurns += dropped;
        _state.CompressionCount++;
        _state.LastCompression = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _state.HistoryTokens = result.Sum(m => EstimateTokens(m.GetValueOrDefault("content", ""))) + _state.SpliceGapTokens;
        _state.TotalTokens = sysTokens + _state.HistoryTokens + curTokens;

        return (true, result, dropped);
    }

    private static double ComputeTransitionEntropy(string prevContent, string nextContent)
    {
        var a = new HashSet<string>(prevContent.Split(' ', '\n', '\r')
            .Where(w => w.Length >= 2).Select(w => w.ToLowerInvariant()));
        var b = new HashSet<string>(nextContent.Split(' ', '\n', '\r')
            .Where(w => w.Length >= 2).Select(w => w.ToLowerInvariant()));
        if (a.Count == 0 || b.Count == 0) return 0.5;
        var overlap = a.Intersect(b).Count();
        var jaccard = (double)overlap / (a.Count + b.Count - overlap);
        return 1.0 - jaccard;
    }

    private static string BuildSpliceGuard(string prevContent, string nextContent)
    {
        var prevSnippet = prevContent.Length > 150 ? prevContent[..150] : prevContent;
        var nextSnippet = nextContent.Length > 150 ? nextContent[..150] : nextContent;

        var prevTopic = ExtractTopic(prevSnippet);
        var nextTopic = ExtractTopic(nextSnippet);

        if (string.IsNullOrWhiteSpace(prevTopic) || string.IsNullOrWhiteSpace(nextTopic))
            return "";

        return prevTopic != nextTopic
            ? $"Context shifts from '{prevTopic}' to '{nextTopic}'"
            : $"Continuing discussion of '{prevTopic}'";
    }

    private static string ExtractTopic(string text)
    {
        var keywords = new[] { "code", "代码", "file", "文件", "error", "错误", "fix", "修复",
            "api", "接口", "database", "数据库", "test", "测试", "deploy", "部署",
            "config", "配置", "search", "搜索", "query", "查询", "review", "审查" };

        foreach (var kw in keywords)
        {
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return kw;
        }

        var words = text.Split(' ', '\n', '\r');
        var longWords = words.Where(w => w.Length >= 4).Take(3).ToList();
        return longWords.Count > 0 ? string.Join(" ", longWords) : "";
    }

    private static string SummarizeMessage(string role, string content)
    {
        if (string.IsNullOrEmpty(content))
            return $"[{role}]: (empty)";
        return content.Length <= 80 ? $"[{role}]: {content}" : $"[{role}]: {content[..80]}...";
    }

    private void CopyState(BudgetState source)
    {
        _state.TotalTokens = source.TotalTokens;
        _state.SystemTokens = source.SystemTokens;
        _state.HistoryTokens = source.HistoryTokens;
        _state.CurrentTokens = source.CurrentTokens;
        _state.MaxTokens = source.MaxTokens;
        _state.MaxTurns = source.MaxTurns;
        _state.TurnCount = source.TurnCount;
        _state.CompressedTurns = source.CompressedTurns;
        _state.LastCompression = source.LastCompression;
        _state.CompressionCount = source.CompressionCount;
        _state.SpliceGapTokens = source.SpliceGapTokens;
        _state.AvgTransitionEntropy = source.AvgTransitionEntropy;
    }
}
