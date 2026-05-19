using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Capability.DocEngine;

public record DocSpec(string Name, string TemplateType, List<DocSectionSpec> Sections, Dictionary<string, object> Metadata);

public record DocSectionSpec(string Name, int Order, string Prompt, bool FoldContext);

public record GenerationProgress(string Section, string Status, string? Content);

public sealed class DocEngine
{
    private readonly ILogger<DocEngine> _logger;
    private readonly Dictionary<string, List<DocSectionSpec>> _templates = new();

    public DocEngine(ILogger<DocEngine>? logger = null)
    {
        _logger = logger ?? NullLogger<DocEngine>.Instance;
        InitializeTemplates();
    }

    private void InitializeTemplates()
    {
        _templates["eia_report"] = new()
        {
            new("项目概况", 1, "Describe the project overview, location, scale, and investment", false),
            new("评价标准", 2, "List applicable environmental standards and regulations", true),
            new("环境现状", 3, "Analyze current environmental conditions including air, water, noise, ecology", true),
            new("工程分析", 4, "Analyze engineering processes, pollution sources, and emission estimates", true),
            new("环境影响预测", 5, "Predict environmental impacts including air dispersion, water quality, noise", true),
            new("防治措施", 6, "Propose pollution prevention and mitigation measures", true),
            new("结论与建议", 7, "Summarize findings and provide recommendations", true)
        };

        _templates["emergency_plan"] = new()
        {
            new("总则", 1, "State the purpose, scope, and principles of the emergency plan", false),
            new("风险分析", 2, "Identify potential risks and hazards", false),
            new("组织机构", 3, "Define the emergency response organization and responsibilities", true),
            new("预防预警", 4, "Describe prevention and early warning mechanisms", true),
            new("应急响应", 5, "Detail the emergency response procedures", true),
            new("后期处置", 6, "Post-incident recovery and assessment", true),
            new("保障措施", 7, "List required resources, training, and drills", true)
        };

        _templates["feasibility"] = new()
        {
            new("项目背景", 1, "Project background and necessity analysis", false),
            new("市场分析", 2, "Market demand and competition analysis", false),
            new("技术方案", 3, "Technical approach and innovation points", true),
            new("投资估算", 4, "Investment estimation and funding plan", true),
            new("效益分析", 5, "Economic, social, and environmental benefits", true),
            new("风险评估", 6, "Risk identification and countermeasures", true),
            new("结论", 7, "Feasibility conclusion and recommendations", true)
        };
    }

    public List<string> GetTemplateTypes() => _templates.Keys.ToList();

    public List<DocSectionSpec> GetTemplate(string type) =>
        _templates.GetValueOrDefault(type) ?? _templates["eia_report"];

    public async Task<string> GenerateAsync(string templateType, Dictionary<string, string> data,
        Func<string, string, Task<string>> chatFn, bool foldContext = false)
    {
        var sections = GetTemplate(templateType);
        var results = new List<string>();
        var foldedContext = "";

        foreach (var section in sections.OrderBy(s => s.Order))
        {
            var contextPrompt = foldContext && !string.IsNullOrEmpty(foldedContext)
                ? $"Previous sections summary:\n{foldedContext}\n\nNow write section: {section.Name}\n{section.Prompt}"
                : $"Write the '{section.Name}' section for a {templateType}.\nBackground: {JsonContent(data)}\n\n{section.Prompt}";

            var content = await chatFn($"doc_gen_{section.Order}", contextPrompt);
            results.Add($"## {section.Name}\n\n{content}");

            if (foldContext)
                foldedContext += $"\\[{section.Name}]: {content[..Math.Min(content.Length, 200)]}\n";
        }

        return string.Join("\n\n", results);
    }

    public async IAsyncEnumerable<GenerationProgress> GenerateStreamingAsync(string templateType,
        Dictionary<string, string> data, Func<string, string, Task<string>> chatFn, bool foldContext = false)
    {
        var sections = GetTemplate(templateType);
        var foldedContext = "";

        foreach (var section in sections.OrderBy(s => s.Order))
        {
            yield return new GenerationProgress(section.Name, "generating", null);

            var contextPrompt = foldContext && !string.IsNullOrEmpty(foldedContext)
                ? $"Previous: {foldedContext}\n\nWrite section: {section.Name}\n{section.Prompt}"
                : $"Write '{section.Name}' for {templateType}.\nContext: {JsonContent(data)}\n\n{section.Prompt}";

            var content = await chatFn($"doc_gen_{section.Order}", contextPrompt);
            yield return new GenerationProgress(section.Name, "completed", content);

            if (foldContext)
                foldedContext += $"\\[{section.Name}]: {content[..Math.Min(content.Length, 200)]}\n";
        }
    }

    private static string JsonContent(Dictionary<string, string> data)
        => string.Join("\n", data.Select(kv => $"{kv.Key}: {kv.Value}"));
}
