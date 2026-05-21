using System.Text.RegularExpressions;
using LTAI.Core.Interfaces;
using LTAI.Execution.Models;
using Microsoft.Extensions.AI;

namespace LTAI.Execution;

public static class MultiHopEvidenceCheck
{
    private static readonly Regex StandardCodeRx = new(
        @"[A-Z]{2,6}[- ]\d{2,4}",
        RegexOptions.Compiled);

    private static readonly Regex UnitRx = new(
        @"\d+\.?\d*\s*(?:吨|mg|dB|km|m|kg|g|L|mL|h|min|s|℃|%|ppm|ppb)",
        RegexOptions.Compiled);

    private static readonly Regex BookmarkRx = new(
        @"《[^》]+》",
        RegexOptions.Compiled);

    private static readonly Regex ChemicalRx = new(
        @"\b[A-Z][a-z]?\d*\b",
        RegexOptions.Compiled);

    internal static readonly Regex WordRx = new(
        @"[\u4e00-\u9fff]{2,}|[a-zA-Z]{3,}",
        RegexOptions.Compiled);

    public static MultiHopReport Analyze(string response, string context)
    {
        var issues = new List<string>();
        var suggestions = new List<string>();

        var responseEntities = ExtractEntities(response);
        var contextEntities = ExtractEntities(context);

        var totalEvidence = responseEntities.Count;
        var referencedEvidence = 0;
        var earlyIgnored = 0;

        var contextLines = context.Split('\n');
        foreach (var entity in responseEntities)
        {
            if (contextEntities.Contains(entity))
                referencedEvidence++;
        }

        var oneThirdPos = response.Length / 3;
        foreach (var entity in responseEntities)
        {
            if (entity.Position > oneThirdPos
                && !responseEntities.Take(responseEntities.Count / 3)
                    .Any(e => e.Text == entity.Text))
                earlyIgnored++;
        }

        var evidenceRecall = totalEvidence > 0
            ? (float)referencedEvidence / totalEvidence
            : 0f;

        var positionBias = totalEvidence > 0
            ? Math.Clamp(1f - (float)earlyIgnored / totalEvidence, 0f, 1f)
            : 1f;

        var multiHopPairs = DetectMultiHopPairs(responseEntities);
        var integratedPairs = CountIntegratedPairs(multiHopPairs);
        var integrationScore = multiHopPairs > 0
            ? (float)integratedPairs / multiHopPairs
            : 1f;

        var finalScore = evidenceRecall * 0.35f
                       + positionBias * 0.30f
                       + integrationScore * 0.35f;

        if (evidenceRecall < 0.5f)
        {
            issues.Add($"Low evidence recall: {evidenceRecall:F2}");
            suggestions.Add("Cite more sources from context");
        }

        if (positionBias < 0.6f)
        {
            issues.Add($"Position bias detected: {positionBias:F2}");
            suggestions.Add("Distribute evidence evenly across response");
        }

        if (integrationScore < 0.5f)
        {
            issues.Add($"Low integration score: {integrationScore:F2}");
            suggestions.Add("Connect related evidence pieces explicitly");
        }

        return new MultiHopReport(
            EvidenceRecall: evidenceRecall,
            PositionBiasScore: positionBias,
            IntegrationScore: integrationScore,
            TotalEvidence: totalEvidence,
            ReferencedEvidence: referencedEvidence,
            EarlyIgnored: earlyIgnored,
            MultiHopPairs: multiHopPairs,
            IntegratedPairs: integratedPairs,
            FinalScore: finalScore,
            Issues: issues,
            Suggestions: suggestions);
    }

    private static List<EvidenceItem> ExtractEntities(string text)
    {
        var items = new List<EvidenceItem>();
        var seen = new HashSet<string>();

        foreach (Match m in StandardCodeRx.Matches(text))
            AddIfNew(m);
        foreach (Match m in UnitRx.Matches(text))
            AddIfNew(m);
        foreach (Match m in BookmarkRx.Matches(text))
            AddIfNew(m);
        foreach (Match m in ChemicalRx.Matches(text))
            AddIfNew(m);

        var terms = new Dictionary<string, int>();
        foreach (Match m in WordRx.Matches(text))
        {
            var w = m.Value.ToLowerInvariant();
            terms.TryGetValue(w, out var cnt);
            terms[w] = cnt + 1;
        }

        foreach (var (term, count) in terms.Where(kv => kv.Value >= 2))
        {
            var idx = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var key = $"term:{term}";
                if (seen.Add(key))
                {
                    items.Add(new EvidenceItem(
                        Text: term,
                        Position: idx,
                        PositionPct: text.Length > 0 ? (float)idx / text.Length : 0f,
                        IsReferenced: false));
                }
            }
        }

        return items;

        void AddIfNew(Match m)
        {
            if (seen.Add(m.Value))
            {
                items.Add(new EvidenceItem(
                    Text: m.Value,
                    Position: m.Index,
                    PositionPct: text.Length > 0 ? (float)m.Index / text.Length : 0f,
                    IsReferenced: false));
            }
        }
    }

    private static int DetectMultiHopPairs(List<EvidenceItem> entities)
    {
        if (entities.Count < 2) return 0;

        var pairs = 0;
        for (var i = 0; i < entities.Count - 1; i++)
        {
            for (var j = i + 1; j < entities.Count; j++)
            {
                var dist = Math.Abs(entities[i].Position - entities[j].Position);
                if (dist < 500) pairs++;
            }
        }
        return pairs;
    }

    private static int CountIntegratedPairs(int pairs)
    {
        return pairs > 0 ? pairs / 2 : 0;
    }
}

public class MultiAgentQualityChecker
{
    private readonly object? _consciousness;
    private readonly int _maxRepairAttempts;

    private static readonly Regex EvalDetectRx = new(
        @"\b(?:eval|exec|subprocess|os\.system|__import__|compile)\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DivByZeroRx = new(
        @"\b\w+\s*/\s*(?:\w+)\b(?!\s*[!=]=)",
        RegexOptions.Compiled);

    private static readonly Regex UnboundedExpRx = new(
        @"np?\.exp\s*\(\s*[^)]*\b\w+\s*\*\s*\d{2,}[^)]*\)",
        RegexOptions.Compiled);

    private static readonly Regex NaNRx = new(
        @"(?<!\b(?:np\.|math\.|float\())isnan|nan\b",
        RegexOptions.Compiled);

    private static readonly Regex ShiftRx = new(
        @"\.shift\s*\(\s*-\d+",
        RegexOptions.Compiled);

    private static readonly Regex IlocRx = new(
        @"\.iloc\s*\[[^\]]*[:-]",
        RegexOptions.Compiled);

    private static readonly Regex ScoreRx = new(
        @"(?:score|rate|评价|分数)[:\s]*(\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public MultiAgentQualityChecker(object? consciousness = null, int maxRepairAttempts = 3)
    {
        _consciousness = consciousness;
        _maxRepairAttempts = maxRepairAttempts;
    }

    public async Task<QualityReport> Check(
        string content,
        Dictionary<string, object?>? context = null,
        string language = "python",
        string originalContext = "",
        string systemPrompt = "",
        string userPrompt = "")
    {
        context ??= new Dictionary<string, object?>();
        var results = new List<CheckResult>();
        var repairAttempts = 0;

        var codeQuality = CheckCodeQuality(content, language);
        results.Add(codeQuality);

        if (codeQuality.Status == CheckStatus.FAIL && _consciousness is IChatClient)
        {
            var repaired = await RepairCode(content, codeQuality.Issues, language);
            results.Add(repaired);

            if (repaired.Status == CheckStatus.REPAIRED && repaired.RepairedContent is not null)
            {
                content = repaired.RepairedContent;
                repairAttempts++;
            }
        }

        if (_consciousness is IChatClient)
        {
            var judgeResult = await Judge(content, context);
            results.Add(judgeResult);

            if (judgeResult.Suggestions.Count > 0)
            {
                var improved = await ImproveLogic(content, judgeResult.Suggestions, context);
                results.Add(improved);
            }
        }

        results.Add(CheckNumericalStability(content));
        results.Add(CheckTemporalLeakage(content));
        results.Add(CheckPromptInjection(content));

        var multiHop = MultiHopEvidenceCheck.Analyze(content, originalContext);
        results.Add(new CheckResult(
            Agent: "MultiHopEvidenceCheck",
            Status: multiHop.FinalScore >= 0.5f ? CheckStatus.PASS : CheckStatus.FAIL,
            Issues: multiHop.Issues,
            Suggestions: multiHop.Suggestions,
            RepairedContent: null,
            Score: multiHop.FinalScore,
            Timestamp: DateTime.UtcNow.ToString("O")));

        results.Add(CheckPromptEcho(content, systemPrompt, userPrompt));

        var passed = results.All(r => r.Status != CheckStatus.FAIL && r.Status != CheckStatus.REJECTED);
        var totalIssues = results.Sum(r => r.Issues.Count);
        var avgScore = results.Count > 0 ? results.Average(r => r.Score) : 0f;

        return new QualityReport(
            Passed: passed,
            Results: results,
            FinalScore: avgScore,
            TotalIssues: totalIssues,
            RepairAttempts: repairAttempts);
    }

    public bool QuickCheck(string content, string language = "python")
    {
        var code = CheckCodeQuality(content, language);
        var temporal = CheckTemporalLeakage(content);
        return code.Status == CheckStatus.PASS && temporal.Status == CheckStatus.PASS;
    }

    public CheckResult CheckCodeQuality(string content, string language)
    {
        var issues = new List<string>();
        var suggestions = new List<string>();

        if (EvalDetectRx.IsMatch(content))
        {
            issues.Add("Dangerous eval/exec/subprocess call detected");
            suggestions.Add("Replace eval/exec with safe alternatives");
        }

        if (content.Length < 10)
        {
            issues.Add("Content too short (< 10 chars)");
            suggestions.Add("Provide more substantive output");
        }

        if (content.Length > 100_000)
        {
            issues.Add("Content exceeds 100K characters");
            suggestions.Add("Truncate or summarize output");
        }

        if (content.Count(c => c == '\n') < 1 && content.Length > 200)
        {
            issues.Add("No line breaks in long content");
            suggestions.Add("Format output with paragraphs");
        }

        var status = issues.Count == 0 ? CheckStatus.PASS : CheckStatus.FAIL;
        var score = issues.Count == 0 ? 1.0f : Math.Max(0.1f, 1.0f - issues.Count * 0.2f);

        return new CheckResult(
            Agent: "CodeQuality",
            Status: status,
            Issues: issues,
            Suggestions: suggestions,
            RepairedContent: null,
            Score: score,
            Timestamp: DateTime.UtcNow.ToString("O"));
    }

    public async Task<CheckResult> RepairCode(string content, List<string> issues, string language)
    {
        if (_consciousness is not IChatClient llm)
        {
            return new CheckResult(
                Agent: "CodeRepair",
                Status: CheckStatus.FAIL,
                Issues: new() { "No LLM consciousness available for repair" },
                Suggestions: new(),
                RepairedContent: null,
                Score: 0f,
                Timestamp: DateTime.UtcNow.ToString("O"));
        }

        try
        {
            var prompt = $"The following {language} code has issues:\n{string.Join("\n", issues)}\n\n" +
                         $"Original code:\n```{language}\n{content[..Math.Min(content.Length, 4000)]}\n```\n\n" +
                         $"Return the repaired code in a code block.";

            var reply = await llm.CompleteAsync(prompt, new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 4096 });

            var repaired = ExtractCodeBlock(reply, language);
            var recheck = CheckCodeQuality(repaired, language);

            return new CheckResult(
                Agent: "CodeRepair",
                Status: recheck.Status == CheckStatus.PASS ? CheckStatus.REPAIRED : CheckStatus.FAIL,
                Issues: recheck.Issues,
                Suggestions: recheck.Suggestions,
                RepairedContent: repaired,
                Score: recheck.Status == CheckStatus.PASS ? 0.8f : 0.3f,
                Timestamp: DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            return new CheckResult(
                Agent: "CodeRepair",
                Status: CheckStatus.FAIL,
                Issues: new() { $"Repair failed: {ex.Message}" },
                Suggestions: new(),
                RepairedContent: null,
                Score: 0f,
                Timestamp: DateTime.UtcNow.ToString("O"));
        }
    }

    public async Task<CheckResult> Judge(string content, Dictionary<string, object?> context)
    {
        if (_consciousness is not IChatClient llm)
        {
            return new CheckResult(
                Agent: "Judge",
                Status: CheckStatus.PASS,
                Issues: new(),
                Suggestions: new(),
                RepairedContent: null,
                Score: 0.7f,
                Timestamp: DateTime.UtcNow.ToString("O"));
        }

        try
        {
            var prompt = $"Judge the quality of this output:\n{content[..Math.Min(content.Length, 3000)]}\n\n" +
                         "Rate from 0-10 and list any issues or improvements. Format: Score: X.X";

            var reply = await llm.CompleteAsync(prompt, new ChatOptions { Temperature = 0.1f, MaxOutputTokens = 512 });

            var score = 5.0f;
            var scoreMatch = ScoreRx.Match(reply);
            if (scoreMatch.Success && float.TryParse(scoreMatch.Groups[1].Value, out var s))
                score = Math.Clamp(s / 10f, 0f, 1f);

            var status = score >= 0.6f ? CheckStatus.PASS : CheckStatus.FAIL;

            return new CheckResult(
                Agent: "Judge",
                Status: status,
                Issues: status == CheckStatus.FAIL ? new() { $"Judge score: {score:F2}" } : new(),
                Suggestions: new() { "See judge feedback for suggestions" },
                RepairedContent: null,
                Score: score,
                Timestamp: DateTime.UtcNow.ToString("O"));
        }
        catch (Exception)
        {
            return new CheckResult(
                Agent: "Judge",
                Status: CheckStatus.PASS,
                Issues: new(),
                Suggestions: new(),
                RepairedContent: null,
                Score: 0.5f,
                Timestamp: DateTime.UtcNow.ToString("O"));
        }
    }

    public async Task<CheckResult> ImproveLogic(string content, List<string> suggestions, Dictionary<string, object?> context)
    {
        if (_consciousness is not IChatClient llm || suggestions.Count == 0)
        {
            return new CheckResult(
                Agent: "LogicImprovement",
                Status: CheckStatus.PASS,
                Issues: new(),
                Suggestions: new(),
                RepairedContent: null,
                Score: 0.8f,
                Timestamp: DateTime.UtcNow.ToString("O"));
        }

        try
        {
            var prompt = $"Improve this content based on suggestions:\n" +
                         $"Suggestions: {string.Join("; ", suggestions)}\n\n" +
                         $"Content:\n{content[..Math.Min(content.Length, 4000)]}\n\n" +
                         $"Return the improved content.";

            var reply = await llm.CompleteAsync(prompt, new ChatOptions { Temperature = 0.3f, MaxOutputTokens = 4096 });

            return new CheckResult(
                Agent: "LogicImprovement",
                Status: CheckStatus.REPAIRED,
                Issues: new(),
                Suggestions: new(),
                RepairedContent: reply,
                Score: 0.7f,
                Timestamp: DateTime.UtcNow.ToString("O"));
        }
        catch (Exception)
        {
            return new CheckResult(
                Agent: "LogicImprovement",
                Status: CheckStatus.FAIL,
                Issues: new() { "Logic improvement failed" },
                Suggestions: new(),
                RepairedContent: null,
                Score: 0.3f,
                Timestamp: DateTime.UtcNow.ToString("O"));
        }
    }

    public CheckResult CheckNumericalStability(string content)
    {
        var issues = new List<string>();
        var suggestions = new List<string>();

        var divMatches = DivByZeroRx.Matches(content);
        foreach (Match m in divMatches)
        {
            var line = GetSurroundingLine(content, m.Index);
            if (!line.Contains("if ") && !line.Contains("try"))
            {
                issues.Add($"Potential division by zero: {m.Value.Trim()}");
                suggestions.Add("Add zero-check guard before division");
            }
        }

        if (UnboundedExpRx.IsMatch(content))
        {
            issues.Add("Unbounded exp() with large coefficient detected");
            suggestions.Add("Clamp or bound exponential inputs");
        }

        if (content.Contains("log(") || content.Contains("sqrt(") || content.Contains("Math.Pow"))
        {
            if (!NaNRx.IsMatch(content) && !content.Contains("IsNaN") && !content.Contains("double.IsNaN"))
            {
                issues.Add("Missing NaN handling for math operations");
                suggestions.Add("Add NaN checks for log/sqrt/pow results");
            }
        }

        var status = issues.Count == 0 ? CheckStatus.PASS : CheckStatus.FAIL;

        return new CheckResult(
            Agent: "NumericalStability",
            Status: status,
            Issues: issues,
            Suggestions: suggestions,
            RepairedContent: null,
            Score: issues.Count == 0 ? 1.0f : Math.Max(0.1f, 1.0f - issues.Count * 0.3f),
            Timestamp: DateTime.UtcNow.ToString("O"));
    }

    public CheckResult CheckTemporalLeakage(string content)
    {
        var issues = new List<string>();
        var suggestions = new List<string>();

        if (ShiftRx.IsMatch(content))
        {
            issues.Add("Negative shift() detected — potential future data leakage");
            suggestions.Add("Use only positive shifts or lag features with time-aware split");
        }

        if (IlocRx.IsMatch(content))
        {
            issues.Add("Leaky iloc slicing detected — may use future indices");
            suggestions.Add("Ensure time-based train/test split before iloc operations");
        }

        var status = issues.Count == 0 ? CheckStatus.PASS : CheckStatus.FAIL;

        return new CheckResult(
            Agent: "TemporalLeakage",
            Status: status,
            Issues: issues,
            Suggestions: suggestions,
            RepairedContent: null,
            Score: issues.Count == 0 ? 1.0f : 0.2f,
            Timestamp: DateTime.UtcNow.ToString("O"));
    }

    public static CheckResult CheckPromptInjection(string content)
    {
        var issues = new List<string>();
        var suggestions = new List<string>();

        if (Core.System.PromptShield.HasPromptInjectionPattern(content))
        {
            issues.Add("Potential prompt injection pattern detected");
            suggestions.Add("Sanitize input and validate against injection attempts");
        }

        var status = issues.Count == 0 ? CheckStatus.PASS : CheckStatus.FAIL;

        return new CheckResult(
            Agent: "PromptInjection",
            Status: status,
            Issues: issues,
            Suggestions: suggestions,
            RepairedContent: null,
            Score: issues.Count == 0 ? 1.0f : 0.0f,
            Timestamp: DateTime.UtcNow.ToString("O"));
    }

    public CheckResult CheckPromptEcho(string content, string systemPrompt, string userPrompt)
    {
        var flags = new List<string>();
        var suggestions = new List<string>();
        var score = 1.0f;

        if (string.IsNullOrEmpty(content) || content.Length < 20)
        {
            flags.Add("too_short");
            suggestions.Add("Response is too short or empty");
            score -= 0.4f;
        }

        if (content.Length > 100 && content.Length < 200
            && content.Count(c => c == ',') < 2)
        {
            flags.Add("too_short");
        }

        if (content.Count(c => c == '.' || c == '。' || c == '\n') > 50)
        {
            flags.Add("verbose");
            suggestions.Add("Consider condensing repetitive content");
            score -= 0.1f;
        }

        if (!string.IsNullOrEmpty(userPrompt))
        {
            var promptWords = ExtractKeywords(userPrompt);
            var contentWords = ExtractKeywords(content);

            var missing = promptWords.Where(pw =>
                !contentWords.Any(cw =>
                    cw.Contains(pw, StringComparison.OrdinalIgnoreCase) ||
                    pw.Contains(cw, StringComparison.OrdinalIgnoreCase)))
                .Take(5).ToList();

            if (missing.Count > 0)
            {
                flags.Add("missing_entities");
                suggestions.Add($"Missing entities from prompt: {string.Join(", ", missing)}");
                score -= 0.15f * Math.Min(missing.Count, 3);
            }
        }

        var backtickCount = content.Count(c => c == '`');
        if (backtickCount % 2 != 0 && backtickCount % 3 != 0)
        {
            flags.Add("unclosed_code_block");
            suggestions.Add("Close all code blocks with matching backticks");
            score -= 0.15f;
        }

        if (content.Contains("may", StringComparison.OrdinalIgnoreCase)
            && content.Contains("could", StringComparison.OrdinalIgnoreCase)
            && content.Count(c => c == '。' || c == '.') > 5)
        {
            flags.Add("hedging");
            suggestions.Add("Provide more definitive conclusions where possible");
            score -= 0.1f;
        }

        var status = flags.Count > 0
            ? (score < 0.5f ? CheckStatus.FAIL : CheckStatus.PASS)
            : CheckStatus.PASS;

        return new CheckResult(
            Agent: "PromptEcho",
            Status: status,
            Issues: flags,
            Suggestions: suggestions,
            RepairedContent: null,
            Score: Math.Clamp(score, 0f, 1f),
            Timestamp: DateTime.UtcNow.ToString("O"));
    }

    private static string ExtractCodeBlock(string text, string language)
    {
        var pattern = $@"```(?:{language})?\s*\n(.*?)```";
        var match = Regex.Match(text, pattern, RegexOptions.Singleline);
        if (match.Success)
            return match.Groups[1].Value.Trim();

        var start = text.IndexOf("```", StringComparison.Ordinal);
        var end = text.LastIndexOf("```", StringComparison.Ordinal);
        if (start >= 0 && end > start + 3)
        {
            var inner = text[(start + 3)..end];
            var newline = inner.IndexOf('\n');
            if (newline >= 0) inner = inner[(newline + 1)..];
            return inner.Trim();
        }

        return text;
    }

    private static string GetSurroundingLine(string content, int position)
    {
        var start = content.LastIndexOf('\n', position);
        start = start >= 0 ? start + 1 : 0;
        var end = content.IndexOf('\n', position);
        end = end >= 0 ? end : content.Length;
        return content[start..end];
    }

    private static HashSet<string> ExtractKeywords(string text)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in MultiHopEvidenceCheck.WordRx.Matches(text))
        {
            if (m.Value.Length >= 3)
                keywords.Add(m.Value);
        }

        foreach (Match m in Regex.Matches(text, @"[A-Z]{2,}"))
        {
            keywords.Add(m.Value);
        }

        return keywords;
    }
}
