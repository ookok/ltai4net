using System.Text.RegularExpressions;

namespace LTAI.Tools.DocEngine;

public record SectionSuggestion(string CurrentSection, string NextSection, string Reason, bool DataAvailable);

public record ReviewComment(string Section, string Severity, string Message, int? Line);

public sealed class DocForge
{
    private readonly Dictionary<string, List<string>> _templateSequences = new()
    {
        ["eia_report"] = new() { "项目概况", "评价标准", "环境现状", "工程分析", "环境影响预测", "防治措施", "结论与建议" },
        ["emergency_plan"] = new() { "总则", "风险分析", "组织机构", "预防预警", "应急响应", "后期处置", "保障措施" },
        ["feasibility"] = new() { "项目背景", "市场分析", "技术方案", "投资估算", "效益分析", "风险评估", "结论" }
    };

    public SectionSuggestion SuggestNext(string templateType, string currentSection, Dictionary<string, object>? availableData = null)
    {
        if (!_templateSequences.TryGetValue(templateType, out var sequence))
            return new SectionSuggestion(currentSection, "", "Unknown template type", false);

        var idx = sequence.FindIndex(s => s.Equals(currentSection, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || idx >= sequence.Count - 1)
            return new SectionSuggestion(currentSection, "", "End of template", false);

        var nextSection = sequence[idx + 1];
        var dataAvailable = availableData?.Count > 0;
        return new SectionSuggestion(currentSection, nextSection,
            $"Next in {templateType} sequence", dataAvailable);
    }

    public List<string> CheckCompleteness(string templateType, List<string> completedSections)
    {
        if (!_templateSequences.TryGetValue(templateType, out var sequence)) return new();
        return sequence.Where(s => !completedSections.Any(cs =>
            cs.Equals(s, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public List<ReviewComment> Review(string currentText, string? previousText = null)
    {
        var comments = new List<ReviewComment>();

        if (previousText != null)
        {
            var currentLines = currentText.Split('\n');
            var prevLines = previousText.Split('\n');
            var changed = currentLines.Where(l => !prevLines.Contains(l)).ToList();
            if (changed.Count > 0)
                comments.Add(new ReviewComment("diff", "info", $"{changed.Count} lines changed", null));
        }

        var headings = Regex.Matches(currentText, @"^(#{1,3})\s+(.+)$", RegexOptions.Multiline);
        int lastLevel = 0;
        foreach (Match m in headings)
        {
            var level = m.Groups[1].Length;
            if (level > lastLevel + 1 && lastLevel > 0)
                comments.Add(new ReviewComment("heading", "warning",
                    $"Heading skip: {m.Groups[2].Value} jumps from H{lastLevel} to H{level}", 0));
            lastLevel = level;
        }

        var sectionCount = headings.Count;
        if (sectionCount < 3)
            comments.Add(new ReviewComment("structure", "major", $"Only {sectionCount} sections - document may be too brief", null));

        return comments;
    }

    public static bool ValidateSchema(string content, string schemaType)
    {
        var requiredSections = new Dictionary<string, List<string>>
        {
            ["eia_report"] = new() { "项目概况", "评价标准", "环境现状", "环境影响预测", "防治措施", "结论" }
        };
        if (!requiredSections.TryGetValue(schemaType, out var required)) return true;
        var lower = content.ToLowerInvariant();
        return required.All(s => lower.Contains(s.ToLowerInvariant()));
    }
}
