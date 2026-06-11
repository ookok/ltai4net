using System.Text;
using System.Text.RegularExpressions;

namespace LTAI.Agent.FusionRoute;

/// <summary>
/// FusionRoute-inspired response span router.
/// Parses L1 response into spans, scores each for uncertainty,
/// and routes low-confidence spans to L2 for refinement.
/// 
/// Instead of "discard L1 response and regenerate everything with L2",
/// this implements token-adjacent span-level collaboration:
/// confident spans pass through, uncertain spans get refined.
/// </summary>
public sealed class ResponseSpanRouter
{
    // Patterns that indicate low-confidence spans
    private static readonly Regex[] _uncertaintyMarkers =
    [
        new(@"\b(不确定|可能|也许|大概|估计|似乎|应该是|按理说|推测|猜测|看样子|貌似|好像|像是|或许是)\b", RegexOptions.Compiled),
        new(@"\b(建议你|推荐|可以尝试|试试|不妨|不妨试试|你可以|你可以考虑)\b", RegexOptions.Compiled),
        new(@"\b(无法获取|无法确定|无法提供|无法访问|没有权限|没有找到|找不到|没找到)\b", RegexOptions.Compiled),
        new(@"\b(需要更多|请提供|请确认|需要确认|需要你|请告诉我更多)\b", RegexOptions.Compiled),
        new(@"\b(我不确定|我不清楚|我不太|我不太清楚|我不太确定|我不太明白|我不是很确定)\b", RegexOptions.Compiled),
        new(@"\b(could|cannot|can't|unable|unsure|may|might|perhaps|probably|possibly|maybe)\b", RegexOptions.Compiled),
        new(@"\b(I'm not sure|I don't know|I'm not certain|I'm unsure|I'm not confident|I'm not familiar)\b", RegexOptions.Compiled),
        new(@"\b(please provide|please confirm|you need to|I would recommend|I'd suggest|it depends)\b", RegexOptions.Compiled),
        // Japanese uncertainty markers
        new(@"\b(かもしれない|でしょう|でしょうね|たぶん|おそらく|多分|不明|わかりません|知りません)\b", RegexOptions.Compiled),
        // Korean uncertainty markers
        new(@"\b(아마도|모르겠습니다|모를|不确定|모르다|할지도)\b", RegexOptions.Compiled),
        // Generic template/placeholder markers (cross-language)
        new(@"(?i)\{\{.*?\}\}|\[TODO\]|<TODO>|___+", RegexOptions.Compiled),
    ];

    // Patterns that indicate high-confidence spans
    private static readonly Regex[] _confidenceMarkers =
    [
        new(@"\b(肯定|一定|绝对|毫无疑问|明确|确实|事实是|实际上|本质上)\b", RegexOptions.Compiled),
        new(@"\b(certainly|definitely|absolutely|undoubtedly|indeed|clearly|obviously)\b", RegexOptions.Compiled),
        new(@"```[\s\S]*?```", RegexOptions.Compiled), // code blocks are usually confident
    ];

    /// <summary>
    /// A single span within an L1 response, with uncertainty score.
    /// </summary>
    public sealed record L1Span(
        string Text,
        double UncertaintyScore, // 0.0 (confident) to 1.0 (very uncertain)
        bool HasCode,
        bool HasToolResult,
        string[] TriggerMarkers); // Which patterns triggered the uncertainty

    /// <summary>
    /// Split L1 response into spans, score each for uncertainty.
    /// </summary>
    public List<L1Span> ParseSpans(string l1Response, string[]? toolCallNames = null)
    {
        var rawSpans = SplitIntoSpans(l1Response);
        var spans = new List<L1Span>();

        foreach (var span in rawSpans)
        {
            var triggers = new List<string>();
            double maxScore = 0;

            // Check uncertainty markers
            foreach (var marker in _uncertaintyMarkers)
            {
                var m = marker.Match(span);
                if (m.Success)
                {
                    triggers.Add(m.Value);
                    maxScore = Math.Max(maxScore, 0.6);
                }
            }

            // Check confidence markers (reduce score)
            foreach (var marker in _confidenceMarkers)
            {
                if (marker.IsMatch(span))
                {
                    maxScore = Math.Max(0, maxScore - 0.3);
                }
            }

            // Check for code blocks (usually confident)
            var hasCode = span.Contains("```") || span.Contains("`");

            // Check for tool result references
            var hasToolResult = toolCallNames != null &&
                toolCallNames.Any(t => span.Contains(t, StringComparison.OrdinalIgnoreCase));

            // Length heuristic: very short spans are likely not substantive
            if (span.Trim().Length < 10 && maxScore == 0)
                maxScore = 0.0;

            // Span with concrete data (numbers, specific names) is more confident
            if (Regex.IsMatch(span, @"\d+\.\d+|\b[A-Z][a-z]+(?:-[A-Z][a-z]+)*\b") && maxScore > 0)
                maxScore = Math.Max(0, maxScore - 0.2);

            spans.Add(new L1Span(
                Text: span,
                UncertaintyScore: Math.Round(maxScore, 2),
                HasCode: hasCode,
                HasToolResult: hasToolResult,
                TriggerMarkers: triggers.ToArray()));
        }

        return spans;
    }

    /// <summary>
    /// Route uncertain spans to L2 for refinement.
    /// Returns a response where uncertain spans are tagged with fusion markers
    /// that L2 will fill in.
    /// </summary>
    public SpanRoutingResult Route(List<L1Span> spans, double threshold = 0.4)
    {
        var confidentParts = new List<string>();
        var uncertainSpans = new List<L1Span>();
        var template = new StringBuilder();

        foreach (var span in spans)
        {
            if (span.UncertaintyScore >= threshold)
            {
                uncertainSpans.Add(span);
                template.AppendLine($"<fusion_span id=\"{uncertainSpans.Count}\"></fusion_span>");
            }
            else
            {
                template.Append(span.Text);
            }
        }

        return new SpanRoutingResult(
            ConfidentParts: confidentParts,
            UncertainSpans: uncertainSpans,
            Template: template.ToString(),
            SpanCount: spans.Count,
            UncertainCount: uncertainSpans.Count,
            UncertaintyRatio: spans.Count > 0 ? (double)uncertainSpans.Count / spans.Count : 0);
    }

    /// <summary>
    /// Build L2 prompt for refining a specific uncertain span.
    /// FusionRoute-inspired: L2 gets context + the specific span to fix.
    /// </summary>
    public string BuildSpanRefinePrompt(string originalQuery, L1Span span, string fullL1Response)
    {
        var sb = new StringBuilder();
        sb.AppendLine("A Flash assistant generated a response, but this specific part needs improvement:");
        sb.AppendLine();
        sb.AppendLine("## Original Query");
        sb.AppendLine(originalQuery);
        sb.AppendLine();
        sb.AppendLine("## Flash Response Context");
        sb.AppendLine(fullL1Response);
        sb.AppendLine();
        sb.AppendLine("## Span to Improve");
        sb.AppendLine(span.Text);
        sb.AppendLine();

        if (span.TriggerMarkers.Length > 0)
        {
            sb.AppendLine("## Signals Triggering Improvement");
            foreach (var marker in span.TriggerMarkers)
                sb.AppendLine($"- Detected uncertainty marker: \"{marker}\"");
            sb.AppendLine();
        }

        if (span.HasCode)
        {
            sb.AppendLine("Note: This span contains code — verify correctness and completeness.");
            sb.AppendLine();
        }

        sb.AppendLine("## Instructions");
        sb.AppendLine("1. Keep the structure and meaning of the original");
        sb.AppendLine("2. Replace uncertainty with confident, factual information");
        sb.AppendLine("3. Use tools if needed to verify facts");
        sb.AppendLine("4. Return ONLY the improved span text (no preamble, no explanation)");

        return sb.ToString();
    }

    /// <summary>
    /// Build a single L2 prompt that refines ALL uncertain spans in one pass.
    /// More efficient than per-span routing when there are few uncertain spans.
    /// </summary>
    public string BuildBatchRefinePrompt(string originalQuery, List<L1Span> uncertainSpans, string fullL1Response)
    {
        var sb = new StringBuilder();
        sb.AppendLine("A Flash assistant generated a response, but some parts need improvement.");
        sb.AppendLine("Below each span is marked with <fusion_span id=\"N\">. Rewrite ONLY the uncertain spans.");
        sb.AppendLine();
        sb.AppendLine("## Original Query");
        sb.AppendLine(originalQuery);
        sb.AppendLine();
        sb.AppendLine("## Flash Response with Fusion Marks");
        sb.AppendLine(fullL1Response);

        // Mark uncertain spans in the response
        var markedResponse = fullL1Response;
        for (int i = 0; i < uncertainSpans.Count; i++)
        {
            var span = uncertainSpans[i];
            markedResponse = markedResponse.Replace(span.Text,
                $"<fusion_span id=\"{i + 1}\">{span.Text}</fusion_span>");
        }
        sb.AppendLine();
        sb.AppendLine("## Marked Response");
        sb.AppendLine(markedResponse);
        sb.AppendLine();
        sb.AppendLine("## Instructions");
        sb.AppendLine("Return the FULL response with only the <fusion_span>...</fusion_span> parts improved.");
        sb.AppendLine("Keep all other text exactly as-is. Remove the fusion_span tags from the output.");

        return sb.ToString();
    }

    /// <summary>
    /// Stitch L1 confident spans with L2 refined spans.
    /// #1 DLLG: adaptive fusion ratio — low-uncertainty spans blend more L1,
    /// high-uncertainty spans blend more L2.
    /// </summary>
    public string Stitch(IReadOnlyList<L1Span> originalSpans,
        List<L1Span> uncertainSpans,
        IReadOnlyList<string> refinedTexts,
        double threshold = 0.4)
    {
        var sb = new StringBuilder();
        int uncertainIdx = 0;

        foreach (var span in originalSpans)
        {
            if (span.UncertaintyScore >= threshold)
            {
                var refined = uncertainIdx < refinedTexts.Count
                    ? refinedTexts[uncertainIdx].Trim()
                    : span.Text;
                // #1 DLLG: adaptive fusion — blend L1 + L2 proportionally to uncertainty
                var fusionRatio = Math.Min(1, span.UncertaintyScore); // how much L2 to use
                if (fusionRatio < 0.7 && refined.Length > 10 && span.Text.Length > 10)
                {
                    // Partial fusion: keep L1 structure, inject L2 corrections
                    var l1Lines = span.Text.Split('\n');
                    var l2Lines = refined.Split('\n');
                    var fused = new List<string>();
                    for (int i = 0; i < Math.Min(l1Lines.Length, l2Lines.Length); i++)
                        fused.Add(l2Lines[i].Length > l1Lines[i].Length * 1.3 ? l2Lines[i] : l1Lines[i]);
                    sb.Append(string.Join('\n', fused));
                }
                else
                {
                    sb.Append(fusionRatio >= 0.7 ? refined : span.Text);
                }
                uncertainIdx++;
            }
            else
            {
                sb.Append(span.Text);
            }
        }

        return sb.ToString();
    }

    // ── private ──

    private static List<string> SplitIntoSpans(string text)
    {
        var spans = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return spans;

        // First, extract code blocks as single spans
        var codeBlocks = new List<(int start, int end, string content)>();
        var codeRegex = new Regex(@"```[\s\S]*?```");
        foreach (Match m in codeRegex.Matches(text))
        {
            codeBlocks.Add((m.Index, m.Index + m.Length, m.Value));
        }

        // Split by sentence boundaries, but keep code blocks intact
        var sentences = Regex.Split(text, @"(?<=[。！？.!?\n])\s*");
        foreach (var sentence in sentences)
        {
            if (string.IsNullOrWhiteSpace(sentence)) continue;

            // Check if this sentence overlaps with a code block
            var isInsideCodeBlock = false;
            foreach (var (start, end, content) in codeBlocks)
            {
                // Approximate: if sentence contains code block marker
                if (sentence.Contains("```"))
                {
                    isInsideCodeBlock = true;
                    break;
                }
            }

            if (isInsideCodeBlock)
            {
                // Emit code blocks as single spans
                foreach (var (start, end, content) in codeBlocks)
                {
                    if (sentence.Contains(content) || content.Contains(sentence.Trim()))
                    {
                        // Only add each code block once
                        if (!spans.Any(s => s.Contains(content)))
                            spans.Add(content.Trim());
                    }
                }
            }
            else
            {
                // Check for list items (they should be kept together)
                if (sentence.TrimStart().StartsWith("- ") ||
                    sentence.TrimStart().StartsWith("* ") ||
                    (sentence.TrimStart().Length > 0 && char.IsDigit(sentence.TrimStart()[0]) &&
                     sentence.TrimStart().Contains(". ")))
                {
                    spans.Add(sentence.Trim());
                }
                else
                {
                    // Regular sentence
                    var trimmed = sentence.Trim();
                    if (trimmed.Length > 0)
                        spans.Add(trimmed);
                }
            }
        }

        // Merge very short spans (< 5 chars) with neighbors
        var merged = new List<string>();
        for (int i = 0; i < spans.Count; i++)
        {
            if (i > 0 && spans[i].Length < 5)
            {
                merged[^1] = merged[^1] + " " + spans[i];
            }
            else
            {
                merged.Add(spans[i]);
            }
        }

        return merged;
    }
}

/// <summary>
/// Result of routing L1 response spans.
/// </summary>
public sealed record SpanRoutingResult(
    List<string> ConfidentParts,
    List<ResponseSpanRouter.L1Span> UncertainSpans,
    string Template,
    int SpanCount,
    int UncertainCount,
    double UncertaintyRatio)
{
    public bool NeedsRouting => UncertainCount > 0;
}
