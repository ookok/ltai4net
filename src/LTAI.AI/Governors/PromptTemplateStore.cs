using System.Collections.Concurrent;
using LTAI.Core.System;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace LTAI.AI.Governors;

public sealed class PromptTemplateStore
{
    private readonly ConcurrentDictionary<string, SimplePromptTemplate> _templates = new();
    private readonly string _promptsDir;
    private readonly ILogger<PromptTemplateStore>? _logger;

    private static readonly Regex TemplateSection = new(@"##\s+template\s*\n(.*?)(?=##\s+\w|\z)", RegexOptions.Singleline | RegexOptions.Compiled);

    public PromptTemplateStore(string? promptsDir = null, ILogger<PromptTemplateStore>? logger = null)
    {
        _promptsDir = promptsDir ?? Path.Combine(AppContext.BaseDirectory, "prompts");
        _logger = logger;
        LoadAll();
    }

    private void LoadAll()
    {
        if (!Directory.Exists(_promptsDir))
        {
            Directory.CreateDirectory(_promptsDir);
        }

        foreach (var file in Directory.GetFiles(_promptsDir, "*.md"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var content = File.ReadAllText(file);
            var templateBody = ExtractTemplateBody(content) ?? content;
            _templates[name] = new SimplePromptTemplate(templateBody);
            _logger?.LogDebug("Loaded prompt template: {Name} ({Len} chars)", name, templateBody.Length);
        }

        SeedDefaults();

        _logger?.LogInformation("PromptTemplateStore: loaded {Count} templates from {Dir}",
            _templates.Count, _promptsDir);
    }

    private static string? ExtractTemplateBody(string fileContent)
    {
        var match = TemplateSection.Match(fileContent);
        if (!match.Success) return null;
        var body = match.Groups[1].Value.Trim();
        return body.Length > 0 ? body : null;
    }

    private void SeedDefaults()
    {
        var defaults = new Dictionary<string, string>
        {
            ["layer1_tool_summary"] = "你可以使用以下工具: {tool_names} 等共 {tool_count} 个。\n【关键规则】上面已经通过自动工具获取了真实数据，你的任务是：\n1) 严格基于上述【Layer1 自动执行工具】的结果回答用户，一字一句都要有数据依据。\n2) 严禁自行推测、联想或编造任何工具结果中不存在的信息。\n3) 如果工具结果为空或报错，必须如实告知，不得猜测原因。\n4) 不得建议用户去执行命令——系统已经执行过了。",

            ["layer_tool_rules"] = "你可以使用以下工具: {tool_names} 等共 {tool_count} 个。\n重要规则: 1) 遇到需要实时信息、外部数据或事实核查的问题，必须先调用工具再回答。\n2) 回答时只能陈述工具返回的事实数据，严禁自行推测、联想或编造任何信息。\n3) 如果工具返回空结果或不确定信息，必须如实告知用户'未找到相关信息'。\n4) 调用工具时，使用格式: 【TOOL:工具名 参数1=值1 参数2=值2】。例如: 【TOOL:web_search query=吉奥环朋 maxResults=5】或【TOOL:shell_exec command=ls】。一行只能写一个TOOL。",

            ["followup_system"] = "You generate relevant follow-up questions. Given a tool result and answer, suggest 2-3 natural follow-up questions in the user's language. Output ONLY the numbered questions, one per line. No intro.",

            ["followup_user"] = "Answer: {answer}\n\nContext: {context}\n\nGenerate 2-3 natural follow-up questions the user might ask next.",
        };

        foreach (var (name, content) in defaults)
        {
            if (!_templates.ContainsKey(name))
                _templates[name] = new SimplePromptTemplate(content);

            var path = Path.Combine(_promptsDir, $"{name}.md");
            if (!File.Exists(path))
            {
                try { File.WriteAllText(path, $"# prompt: {name}\n## template\n{content}\n"); } catch { }
            }
        }

        _logger?.LogInformation("PromptTemplateStore: seeded {Count} default templates", defaults.Count);
    }

    public string Render(string templateName, IReadOnlyDictionary<string, string>? variables = null)
    {
        if (_templates.TryGetValue(templateName, out var template))
            return template.Render(variables ?? new Dictionary<string, string>());

        _logger?.LogWarning("Prompt template not found: {Name}", templateName);
        return "";
    }

    public bool HasTemplate(string name) => _templates.ContainsKey(name);

    public void Reload()
    {
        _templates.Clear();
        LoadAll();
    }

    public IReadOnlyList<string> ListTemplates() => _templates.Keys.ToList();
}
