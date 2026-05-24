using System.Collections.Concurrent;
using LTAI.Core.System;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class PromptTemplateStore
{
    private readonly ConcurrentDictionary<string, SimplePromptTemplate> _templates = new();
    private readonly string _promptsDir;
    private readonly ILogger<PromptTemplateStore>? _logger;

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

        foreach (var file in Directory.GetFiles(_promptsDir, "*.prompt"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var content = File.ReadAllText(file);
            _templates[name] = new SimplePromptTemplate(content);
            _logger?.LogDebug("Loaded prompt template: {Name} ({Len} chars)", name, content.Length);
        }

        SeedDefaults(); // Always ensure required templates exist

        _logger?.LogInformation("PromptTemplateStore: loaded {Count} templates from {Dir}",
            _templates.Count, _promptsDir);
    }

    private void SeedDefaults()
    {
        var defaults = new Dictionary<string, string>
        {
            ["layer1_tool_summary"] = "你可以使用以下工具: {tool_names} 等共 {tool_count} 个。\n【关键规则】上面已经通过自动工具获取了真实数据，你的任务是：\n1) 严格基于上述【Layer1 自动执行工具】的结果回答用户，一字一句都要有数据依据。\n2) 严禁自行推测、联想或编造任何工具结果中不存在的信息。\n3) 如果工具结果为空或报错，必须如实告知，不得猜测原因。\n4) 不得建议用户去执行命令——系统已经执行过了。",

            ["layer_tool_rules"] = "你可以使用以下工具: {tool_names} 等共 {tool_count} 个。\n重要规则: 1) 遇到需要实时信息、外部数据或事实核查的问题，必须先调用工具再回答。\n2) 回答时只能陈述工具返回的事实数据，严禁自行推测、联想或编造任何信息。\n3) 如果工具返回空结果或不确定信息，必须如实告知用户'未找到相关信息'。\n4) 声称使用了工具（如\"已使用shell_exec\"）必须在响应中发出 tool_call，否则视为编造。",

            ["all_layers_empty"] = "【系统提示】所有自动工具和搜索均未能获取到相关数据。你必须如实告知用户当前无法回答该问题。严禁编造任何具体数字、名称或事实。可以建议用户提供更多信息或换个方式提问。",

            ["meta_model_upgrade"] = "【系统自评】该领域熟悉度低（置信度={certainty}，原因: {reason}），已升级到 {model} 处理。请务必使用工具验证信息，不得推测。",

            ["meta_tool_recommend"] = "【系统自评】该领域熟悉度低（置信度={certainty}，原因: {reason}）。请务必使用工具，不得推测。",

            ["plan_system"] = "You are a task planner. Output ONLY a JSON plan. Do NOT answer the query, do NOT add explanations, do NOT use markdown code fences.",

            ["plan_user"] = "Available tools:\n{tools}\n\nUser query: \"{query}\"\n\nOutput ONLY a JSON plan. Format: {{\"plan\":[{{\"tool\":\"name\",\"args\":{{\"p\":\"v\"}}}}]}}\nDo NOT include tools that won't help answer the query.\nIf no tools are needed, output: {{\"plan\":[]}}",

            ["verify_system"] = "You verify factuality. Output ONLY 'YES' or 'NO' followed by a one-line reason.",

            ["verify_user"] = "Tool results:\n---\n{context}\n---\n\nModel answer:\n---\n{response}\n---\n\nIs every factual claim in the model answer directly supported by the tool results?\nAnswer ONLY: YES or NO, followed by a single short reason.",

            ["grounding_failed"] = "【事实核查失败 L{level} - {check_type}】{retry_instruction}",

            ["grounding_failed_severe"] = "【严重警告】【事实核查失败 L{level} - {check_type}】{retry_instruction}",

            ["force_tool_exec"] = "【系统强制工具执行 L{level}】以下是为确保回答准确而强制获取的数据，必须基于此回答：\n{context}",

            ["semantic_verify_failed"] = "【语义验证失败】{retry_instruction}",

            ["auto_search_empty"] = "【自动网络搜索】搜索 \"{query}\" 未找到任何相关结果。你必须如实告知用户未找到相关信息，严禁编造虚构。",

            ["auto_search_results"] = "【自动网络搜索结果】（仅基于以下数据回答，不得自行推测或联想）\n{results}",

            ["honest_fallback"] = "抱歉，经过多次尝试仍无法提供可靠的答案。建议换个方式提问或提供更多具体信息。",

            ["layer2_context_header"] = "【Layer2 自动规划执行】以下是按计划执行的工具结果：",
        };

        foreach (var (name, content) in defaults)
        {
            _templates[name] = new SimplePromptTemplate(content);
            var path = Path.Combine(_promptsDir, $"{name}.prompt");
            if (!File.Exists(path))
            {
                try { File.WriteAllText(path, content); } catch { }
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
