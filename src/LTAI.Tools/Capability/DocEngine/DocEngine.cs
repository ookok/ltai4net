using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tools.DocEngine;

/// <summary>
/// Specification for a complete document to be generated.
/// </summary>
public record DocSpec(string Name, string TemplateType, List<DocSectionSpec> Sections, Dictionary<string, object> Metadata);

/// <summary>
/// A single section within a document template.
/// </summary>
public record DocSectionSpec(string Name, int Order, string Prompt, bool FoldContext);

/// <summary>
/// Progress update during streaming document generation.
/// </summary>
public record GenerationProgress(string Section, string Status, string? Content);

/// <summary>
/// Template-driven document generation engine.
/// Supports built-in EIA/emergency/feasibility templates PLUS any
/// user-registered template via <see cref="RegisterTemplate"/>.
///
/// Each template is a list of <see cref="DocSectionSpec"/> sections
/// generated sequentially. The optional "fold context" feature passes
/// summaries of prior sections into each new section's prompt,
/// enabling coherent multi-section documents.
///
/// Usage:
///   var engine = new DocEngine();
///   engine.RegisterTemplate("api_doc", new List&lt;DocSectionSpec&gt; { ... });
///   var doc = await engine.GenerateAsync("api_doc", data, chatFn);
///
/// Callers: LTAI.Tools.Capability.DocEngine.DocForge, LTAI.Web.InnovationEndpoints,
///          LTAI.Cli.Commands.AutoFixCommand.
/// </summary>
public sealed class DocEngine
{
    private readonly ILogger<DocEngine> _logger;
    private readonly ConcurrentDictionary<string, List<DocSectionSpec>> _templates = new();

    public DocEngine(ILogger<DocEngine>? logger = null)
    {
        _logger = logger ?? NullLogger<DocEngine>.Instance;
        InitializeTemplates();
    }

    /// <summary>
    /// Register a custom document template at runtime.
    /// Enables domain-specific document generation without modifying this class.
    /// Overwrites any existing template with the same name.
    /// </summary>
    public void RegisterTemplate(string name, List<DocSectionSpec> sections)
    {
        _templates[name] = sections;
        _logger.LogInformation("DocEngine: registered template '{Name}' with {Count} sections", name, sections.Count);
    }

    /// <summary>Remove a previously registered template. No-op if not found.</summary>
    public void UnregisterTemplate(string name)
    {
        if (_templates.TryRemove(name, out _))
            _logger.LogInformation("DocEngine: unregistered template '{Name}'", name);
    }

    private void InitializeTemplates()
    {
        // ── Domain-specific: Chinese EIA (Environmental Impact Assessment) ──
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

        // ── Generic templates (language-agnostic, use-case agnostic) ──

        _templates["general_report"] = new()
        {
            new("Executive Summary", 1, "Write an executive summary covering key findings and recommendations", false),
            new("Introduction", 2, "Introduce the subject, scope, and methodology", false),
            new("Background", 3, "Provide context, prior work, and relevant background information", true),
            new("Analysis", 4, "Detailed analysis with data, evidence, and reasoning", true),
            new("Discussion", 5, "Interpret findings, discuss implications and trade-offs", true),
            new("Conclusion", 6, "Summarize findings and state conclusions", true),
            new("Recommendations", 7, "Provide actionable recommendations based on the analysis", true)
        };

        _templates["api_doc"] = new()
        {
            new("Overview", 1, "API overview: purpose, base URL, authentication, versioning", false),
            new("Endpoints", 2, "List all endpoints with methods, paths, and brief descriptions", false),
            new("Authentication", 3, "Authentication methods, token format, and authorization scopes", false),
            new("Request Format", 4, "Request structure: headers, query parameters, request body schemas", true),
            new("Response Format", 5, "Response structure: status codes, response body schemas, error formats", true),
            new("Error Handling", 6, "Error codes, error response format, retry strategy", true),
            new("Examples", 7, "Complete request/response examples for common use cases", true),
            new("Rate Limits", 8, "Rate limiting policy, quota management, and best practices", true)
        };

        _templates["technical_spec"] = new()
        {
            new("Introduction", 1, "Purpose, scope, definitions, and references", false),
            new("Architecture Overview", 2, "System architecture, components, and data flow", false),
            new("Component Design", 3, "Detailed design of each component: interfaces, dependencies, states", true),
            new("Data Model", 4, "Data models, schemas, relationships, and storage details", true),
            new("API Specification", 5, "Public API surface: endpoints, contracts, error codes", true),
            new("Security", 6, "Security model: authentication, authorization, data protection", true),
            new("Deployment", 7, "Deployment requirements, configuration, and operational notes", true),
            new("Testing Strategy", 8, "Testing approach: unit, integration, e2e, and performance", true)
        };
    }

    /// <summary>Get all registered template type names.</summary>
    public List<string> GetTemplateTypes() => _templates.Keys.ToList();

    /// <summary>
    /// Get a template by type name. Falls back to "general_report" if not found.
    /// </summary>
    public List<DocSectionSpec> GetTemplate(string type) =>
        _templates.GetValueOrDefault(type) ?? _templates.GetValueOrDefault("general_report", _templates.First().Value);

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
