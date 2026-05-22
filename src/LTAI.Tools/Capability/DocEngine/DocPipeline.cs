using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tools.DocEngine;

public record PipelineStage(string Name, string Status, string? Output, string? Error);

public record TemplateEntry(string Name, string Category, string Description, List<string> Sections,
    Dictionary<string, string>? Metadata);

public enum BumpKind { Major, Minor, Patch }

public sealed class DocumentPipeline
{
    private readonly DocForge _forge;
    private readonly ILogger<DocumentPipeline> _logger;

    public DocumentPipeline(DocForge? forge = null, ILogger<DocumentPipeline>? logger = null)
    {
        _forge = forge ?? new DocForge();
        _logger = logger ?? NullLogger<DocumentPipeline>.Instance;
    }

    public List<PipelineStage> Run(string content, string templateType)
    {
        return new()
        {
            new("lint", "completed", RunLint(content), null),
            new("consistency", "completed", RunConsistencyCheck(content, templateType), null),
            new("coverage", "completed", CheckCoverage(content, templateType), null),
            new("schema", "completed", RunSchemaCheck(content, templateType), null),
            new("build", "completed", content, null)
        };
    }

    private string RunLint(string content)
    {
        var issues = new List<string>();
        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 200) issues.Add($"Line {i + 1}: too long ({lines[i].Length} chars)");
            if (lines[i].TrimEnd().Length != lines[i].Length) issues.Add($"Line {i + 1}: trailing whitespace");
        }
        return issues.Count == 0 ? "PASS" : string.Join("; ", issues.Take(5));
    }

    private string RunConsistencyCheck(string content, string templateType)
    {
        var comments = _forge.Review(content);
        var blockers = comments.Count(c => c.Severity == "major");
        return blockers == 0 ? "PASS" : $"{blockers} major issues";
    }

    private string CheckCoverage(string content, string templateType)
    {
        var requirements = new Dictionary<string, Func<string, bool>>
        {
            ["Has title"] = c => c.Contains("##"),
            ["Has introduction"] = c => c.Contains("概述", StringComparison.OrdinalIgnoreCase) || c.Contains("introduction", StringComparison.OrdinalIgnoreCase),
            ["Has conclusion"] = c => c.Contains("结论", StringComparison.OrdinalIgnoreCase) || c.Contains("conclusion", StringComparison.OrdinalIgnoreCase),
            ["Has data"] = c => System.Text.RegularExpressions.Regex.IsMatch(c, @"\d+"),
            ["Min length"] = c => c.Length > 200
        };

        var passed = requirements.Count(r => r.Value(content));
        return $"{passed}/{requirements.Count} checks passed";
    }

    private string RunSchemaCheck(string content, string templateType)
        => DocForge.ValidateSchema(content, templateType) ? "PASS" : "FAIL";

    public static (int Major, int Minor, int Patch) BumpVersion(BumpKind kind, int major, int minor, int patch)
        => kind switch
        {
            BumpKind.Major => (major + 1, 0, 0),
            BumpKind.Minor => (major, minor + 1, 0),
            BumpKind.Patch => (major, minor, patch + 1),
            _ => (major, minor, patch)
        };

    public static BumpKind AutoBump(string oldText, string newText)
    {
        var added = newText.Length - oldText.Length;
        if (Math.Abs(added) > 500) return BumpKind.Major;
        if (Math.Abs(added) > 100) return BumpKind.Minor;
        return BumpKind.Patch;
    }
}

public sealed class TemplateRegistry
{
    private readonly Dictionary<string, TemplateEntry> _templates = new();

    public TemplateRegistry()
    {
        RegisterDefaults();
    }

    private void RegisterDefaults()
    {
        Register(new TemplateEntry("eia_report", "environment", "Environmental Impact Assessment",
            new() { "项目概况", "评价标准", "环境现状", "工程分析", "环境影响预测", "防治措施", "结论与建议" },
            new() { ["language"] = "zh-CN", ["industry"] = "environmental" }));

        Register(new TemplateEntry("emergency_plan", "safety", "Emergency Response Plan",
            new() { "总则", "风险分析", "组织机构", "预防预警", "应急响应", "后期处置", "保障措施" },
            new() { ["language"] = "zh-CN", ["industry"] = "safety" }));

        Register(new TemplateEntry("feasibility", "business", "Feasibility Study Report",
            new() { "项目背景", "市场分析", "技术方案", "投资估算", "效益分析", "风险评估", "结论" },
            new() { ["language"] = "zh-CN", ["industry"] = "business" }));
    }

    public void Register(TemplateEntry entry) => _templates[entry.Name] = entry;

    public List<TemplateEntry> Search(string? query = null)
    {
        if (query == null) return _templates.Values.ToList();
        var lower = query.ToLowerInvariant();
        return _templates.Values
            .Where(t => t.Name.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
                        t.Category.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
                        t.Description.Contains(lower, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public TemplateEntry? Get(string name) => _templates.GetValueOrDefault(name);
}

public sealed class GenerationBudget
{
    private int _dailyTokens;
    private readonly int _dailyLimit;
    private int _resetDay;

    public GenerationBudget(int dailyLimit = 1_000_000)
    {
        _dailyLimit = dailyLimit;
        _resetDay = DateTime.UtcNow.DayOfYear;
    }

    public bool Allocate(int tokens)
    {
        if (DateTime.UtcNow.DayOfYear != _resetDay) { _dailyTokens = 0; _resetDay = DateTime.UtcNow.DayOfYear; }
        if (_dailyTokens + tokens > _dailyLimit) return false;
        _dailyTokens += tokens;
        return true;
    }

    public int Remaining => _dailyLimit - _dailyTokens;
    public double UsagePct => (double)_dailyTokens / _dailyLimit * 100;
    public int SuggestAllocation(int maxRequested) => Math.Min(maxRequested, Remaining);
}
