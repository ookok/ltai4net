using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

public sealed class L0IntentClassifier
{
    private readonly RuleLoader _loader;
    private readonly ILogger<L0IntentClassifier> _logger;
    private IReadOnlyList<IntentRule> _rules = Array.Empty<IntentRule>();
    private readonly string _rulesDir;

    public L0IntentClassifier(RuleLoader? loader = null, string? rulesDir = null, ILogger<L0IntentClassifier>? logger = null)
    {
        _rulesDir = rulesDir ?? Path.Combine(AppContext.BaseDirectory, "rules");
        _loader = loader ?? new RuleLoader(_rulesDir, logger as ILogger<RuleLoader>);
        _logger = logger ?? NullLogger<L0IntentClassifier>.Instance;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _rules = await _loader.LoadAllAsync(ct).ConfigureAwait(false);
        if (_rules.Count == 0)
            _logger.LogWarning("No intent rules loaded from {Dir}, using built-in fallback", _rulesDir);
    }

    public string Classify(string query) => ClassifyWithConfidence(query).Domain;

    public (string Domain, float Confidence) ClassifyWithConfidence(string query)
    {
        var rules = _rules;
        if (rules.Count == 0)
            return (FallbackClassify(query), 0.3f);

        var text = query.ToLowerInvariant();
        var bestDomain = "general";
        var bestScore = 0;
        var maxPossible = 0;

        foreach (var rule in rules)
        {
            var score = 0;
            var ruleMax = rule.Keywords.Length + rule.Patterns.Length * 2;

            foreach (var kw in rule.Keywords)
            {
                if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    score++;
            }

            foreach (var pattern in rule.Patterns)
            {
                if (pattern.IsMatch(query))
                    score += 2;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestDomain = rule.Domain;
                maxPossible = ruleMax;
            }
        }

        var confidence = maxPossible > 0 ? Math.Min((float)bestScore / maxPossible, 1.0f) : 0.0f;

        if (bestScore > 0)
        {
            _logger.LogDebug("Intent '{Domain}' matched (score={Score}/{Max}, confidence={Conf:F2})",
                bestDomain, bestScore, maxPossible, confidence);
        }

        return (bestDomain, confidence);
    }

    public (float Quality, float Speed, float Cost) GetProfile(string domain)
    {
        var rule = _rules.FirstOrDefault(r => r.Domain == domain);
        if (rule != null)
            return (rule.Quality, rule.Speed, rule.Cost);
        return (0.75f, 0.35f, 0.35f);
    }

    public float[] ClassifyToEmbedding(string query, int dim = 768)
    {
        var domain = Classify(query);
        var (q, s, c) = GetProfile(domain);
        var emb = new float[dim];
        var domainBytes = global::System.Text.Encoding.UTF8.GetBytes(domain);
        for (var i = 0; i < Math.Min(domainBytes.Length, dim - 3); i++)
            emb[i] = domainBytes[i] / 255f;
        var queryBytes = global::System.Text.Encoding.UTF8.GetBytes(query);
        var queryOffset = Math.Min(domainBytes.Length + 1, dim - 3);
        for (var i = 0; i < Math.Min(queryBytes.Length, dim - queryOffset - 3); i++)
            emb[queryOffset + i] = queryBytes[i] / 255f;
        emb[dim - 3] = q;
        emb[dim - 2] = s;
        emb[dim - 1] = c;
        return emb;
    }

    public Func<string, CancellationToken, string> ToFunc()
    {
        return (query, _) => Classify(query);
    }

    public IReadOnlyList<IntentRule> Rules => _rules;

    public void HotUpdateKeywords(string domain, string[] keywords, float quality, float speed, float cost)
    {
        var existing = _rules.FirstOrDefault(r => r.Domain == domain);
        if (existing != null)
        {
            var merged = new HashSet<string>(existing.Keywords);
            foreach (var kw in keywords)
                merged.Add(kw);

            var updatedRule = existing with
            {
                Keywords = merged.ToArray(),
                Quality = (existing.Quality * 0.8f + quality * 0.2f),
                Speed = (existing.Speed * 0.8f + speed * 0.2f),
                Cost = (existing.Cost * 0.8f + cost * 0.2f)
            };

            var newRules = _rules.Where(r => r.Domain != domain).Append(updatedRule).ToList();
            _rules = newRules.AsReadOnly();
            _logger.LogInformation("HotUpdate: domain '{Domain}' — {Count} keywords (Q={Q:F2} S={S:F2} C={C:F2})",
                domain, merged.Count, updatedRule.Quality, updatedRule.Speed, updatedRule.Cost);
        }
        else
        {
            var newRule = new IntentRule
            {
                Domain = domain,
                Keywords = keywords,
                Quality = quality,
                Speed = speed,
                Cost = cost,
                Description = $"Evolved rule for {domain}"
            };
            var newRules = _rules.Append(newRule).ToList();
            _rules = newRules.AsReadOnly();
            _logger.LogInformation("HotUpdate: NEW domain '{Domain}' — {Count} keywords", domain, keywords.Length);
        }
    }

    public async Task PersistRulesAsync(CancellationToken ct = default)
    {
        var dir = _rulesDir;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        foreach (var rule in _rules)
        {
            var filePath = Path.Combine(dir, $"{rule.Domain}.md");
            var content = BuildMdContent(rule);
            var tmpPath = filePath + ".tmp";
            await File.WriteAllTextAsync(tmpPath, content, ct).ConfigureAwait(false);
            File.Move(tmpPath, filePath, true);
        }
        _logger.LogInformation("Persisted {Count} intent rules to {Dir}", _rules.Count, dir);
    }

    private static string BuildMdContent(IntentRule rule)
    {
        var lines = new List<string>
        {
            $"# rule: {rule.Domain}",
            "layer: L0",
            $"quality: {rule.Quality:F2}",
            $"speed: {rule.Speed:F2}",
            $"cost: {rule.Cost:F2}",
            $"description: {rule.Description}",
            "",
            "## keywords"
        };
        lines.AddRange(rule.Keywords.Select(k => k));
        lines.Add("");
        lines.Add("## regex");
        lines.AddRange(rule.Patterns.Select(p => p.ToString().TrimStart('/').TrimEnd('/')));
        lines.Add("");
        return string.Join("\n", lines);
    }

    private static string FallbackClassify(string query)
    {
        var text = query.ToLowerInvariant();

        if (text.Contains("代码") || text.Contains("函数") || text.Contains("bug") || text.Contains("class") ||
            text.Contains("code") || text.Contains("function") || text.Contains("debug") || text.Contains("compile") ||
            text.Contains("refactor") || text.Contains("build") || text.Contains("test"))
            return "code";

        if (text.Contains("计算") || text.Contains("公式") || text.Contains("数学") ||
            text.Contains("math") || text.Contains("calculate") || text.Contains("equation") || text.Contains("proof"))
            return "math";

        if (text.Contains("翻译") || text.Contains("translate") || text.Contains("translation"))
            return "translation";

        if (text.Contains("总结") || text.Contains("摘要") || text.Contains("概括") ||
            text.Contains("summarize") || text.Contains("summary") || text.Contains("tldr"))
            return "summarization";

        if (text.Contains("环评") || text.Contains("PM2.5") || text.Contains("排放") ||
            text.Contains("eia") || text.Contains("environment") || text.Contains("emission") || text.Contains("pollution"))
            return "eia";

        if (text.Contains("对话") || text.Contains("聊天") || text.Contains("chat") || text.Contains("conversation") ||
            text.Contains("hello") || text.Contains("help"))
            return "chat";

        if (text.Contains("推理") || text.Contains("逻辑") || text.Contains("reasoning") || text.Contains("analyze") ||
            text.Contains("why") || text.Contains("explain"))
            return "reasoning";

        return "general";
    }
}
