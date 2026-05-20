namespace LTAI.TreeLLM.Prompting;

public sealed class RoleTemplate
{
    public string Name { get; set; } = "";
    public string RolePrompt { get; set; } = "";
    public List<string> FewShotExamples { get; set; } = new();
    public List<string> QualityGates { get; set; } = new();
    public string OutputFormat { get; set; } = "";
}

public sealed class PromptOptimizer
{
    private static readonly Lazy<PromptOptimizer> _instance = new(() => new PromptOptimizer());
    public static PromptOptimizer Instance => _instance.Value;

    private static readonly Dictionary<string, RoleTemplate> RoleTemplates = new()
    {
        ["eia_engineer"] = new()
        {
            Name = "EIA Engineer", RolePrompt = "You are an Environmental Impact Assessment engineer. Follow GB3095, HJ2.2 standards.",
            FewShotExamples = new() { "大气扩散模型参数: AERMOD模式适用于平坦地形。CALPUFF适用于复杂地形。" },
            QualityGates = new() { "标准引用正确", "模型选择合理", "参数来源可溯" },
            OutputFormat = "环评报告格式"
        },
        ["code_reviewer"] = new()
        {
            Name = "Code Reviewer", RolePrompt = "You are a senior code reviewer. Identify bugs, style issues, and improvements.",
            FewShotExamples = new() { "Bug: null reference on line 42", "Style: use var instead of explicit type" },
            QualityGates = new() { "覆盖安全审查", "性能分析", "可维护性建议" },
            OutputFormat = "Markdown with severity labels"
        },
        ["data_analyst"] = new()
        {
            Name = "Data Analyst", RolePrompt = "You are a data analyst. Analyze patterns, trends, and provide insights.",
            QualityGates = new() { "数据来源标注", "统计方法说明", "可视化建议" },
            OutputFormat = "Structured report"
        },
        ["translator"] = new()
        {
            Name = "Translator", RolePrompt = "Translate accurately while preserving tone and context.",
            QualityGates = new() { "术语一致性", "语气保持", "文化适配" },
            OutputFormat = "Bilingual format"
        },
        ["security_auditor"] = new()
        {
            Name = "Security Auditor", RolePrompt = "Audit code and configurations for security vulnerabilities.",
            FewShotExamples = new() { "CWE-79: XSS detected in user input rendering" },
            QualityGates = new() { "OWASP Top 10覆盖", "CVE引用", "修复方案具体" },
            OutputFormat = "Security audit report"
        },
        ["doc_writer"] = new()
        {
            Name = "Documentation Writer", RolePrompt = "Write clear, comprehensive technical documentation.",
            QualityGates = new() { "结构清晰", "示例充分", "API完整" },
            OutputFormat = "Markdown documentation"
        }
    };

    private PromptOptimizer() { }

    public RoleTemplate? GetRole(string role) => RoleTemplates.GetValueOrDefault(role);

    public List<string> ListRoles() => RoleTemplates.Keys.ToList();

    public string BuildSystemPrompt(string role, Dictionary<string, string>? context = null)
    {
        var tmpl = GetRole(role);
        if (tmpl == null) return "";

        var parts = new List<string> { tmpl.RolePrompt };

        if (tmpl.FewShotExamples.Count > 0)
        {
            parts.Add("Examples:");
            foreach (var ex in tmpl.FewShotExamples.Take(3))
                parts.Add($"- {ex}");
        }

        if (tmpl.QualityGates.Count > 0)
            parts.Add($"Quality requirements: {string.Join(", ", tmpl.QualityGates)}");

        if (!string.IsNullOrEmpty(tmpl.OutputFormat))
            parts.Add($"Output format: {tmpl.OutputFormat}");

        if (context != null && context.Count > 0)
            parts.Add($"Context: {System.Text.Json.JsonSerializer.Serialize(context)}");

        return string.Join("\n\n", parts);
    }

    public string PreprocessPrompt(string userInput)
    {
        var patterns = new Dictionary<string, string>
        {
            ["写代码|生成代码|code"] = "code_reviewer",
            ["分析|analysis|analyze"] = "data_analyst",
            ["翻译|translate"] = "translator",
            ["安全|security|漏洞|vuln"] = "security_auditor",
            ["文档|doc|documentation|说明"] = "doc_writer",
            ["环评|环境|EIA|eia"] = "eia_engineer"
        };

        foreach (var (pattern, role) in patterns)
        {
            var keywords = pattern.Split('|');
            if (keywords.Any(k => userInput.ToLower().Contains(k.ToLower())))
                return role;
        }

        return "code_reviewer";
    }

    public string OptimizePrompt(string userInput, int rounds = 2)
    {
        var role = PreprocessPrompt(userInput);
        var systemPrompt = BuildSystemPrompt(role);
        var parts = new List<string> { systemPrompt };
        for (var i = 0; i < rounds; i++)
            parts.Add($"Round {i + 1}: {userInput}");
        return string.Join("\n\n---\n\n", parts);
    }

    public Dictionary<string, string> ExtractContextVars(string userInput)
    {
        var vars = new Dictionary<string, string>();
        var fileMatches = System.Text.RegularExpressions.Regex.Matches(userInput, @"@([^\s]+)");
        foreach (System.Text.RegularExpressions.Match m in fileMatches)
            vars[$"file_{vars.Count}"] = m.Groups[1].Value;

        var templateMatches = System.Text.RegularExpressions.Regex.Matches(userInput, @"\{\{(\w+)\}\}");
        foreach (System.Text.RegularExpressions.Match m in templateMatches)
        {
            var key = m.Groups[1].Value;
            var envVal = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(envVal))
                vars[key] = envVal;
        }

        return vars;
    }
}
