using System.Text.Json;

namespace LTAI.Core.System;

public sealed class RegistryTool
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string Formula { get; set; } = "";
    public Dictionary<string, object> Params { get; set; } = new();
    public string Source { get; set; } = "hardcoded";
    public bool Enabled { get; set; } = true;

    public string ToRoutingText()
    {
        var parts = new List<string> { $"Tool:{Name}. {Description}" };
        if (!string.IsNullOrEmpty(Formula))
            parts.Add($"Formula:{Formula}");
        return string.Join(" ", parts);
    }
}

public sealed class RegistrySkill
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string PromptTemplate { get; set; } = "";
    public string Category { get; set; } = "";
    public string Source { get; set; } = "learned";
    public int SuccessCount { get; set; }
    public int UsageCount { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class RegistryRole
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Capabilities { get; set; } = new();
    public string SystemPrompt { get; set; } = "";
    public string Source { get; set; } = "hardcoded";
    public bool Enabled { get; set; } = true;
}

public sealed class UnifiedRegistry
{
    private static readonly Lazy<UnifiedRegistry> _instance = new(() => new UnifiedRegistry());
    public static UnifiedRegistry Instance => _instance.Value;

    public Dictionary<string, RegistryTool> Tools { get; } = new();
    public Dictionary<string, RegistrySkill> Skills { get; } = new();
    public Dictionary<string, RegistryRole> Roles { get; } = new();
    private readonly Dictionary<string, List<Action<object>>> _subscriptions = new();
    private readonly object _lock = new();

    private UnifiedRegistry() { }

    public void RegisterTool(RegistryTool tool)
    {
        lock (_lock) { Tools[tool.Name] = tool; }
        Notify("tool", tool);
    }

    public void RegisterSkill(RegistrySkill skill)
    {
        lock (_lock) { Skills[skill.Name] = skill; }
        Notify("skill", skill);
    }

    public void RegisterRole(RegistryRole role)
    {
        lock (_lock) { Roles[role.Name] = role; }
        Notify("role", role);
    }

    public void Subscribe(string typeName, Action<object> callback)
    {
        lock (_lock)
        {
            if (!_subscriptions.ContainsKey(typeName))
                _subscriptions[typeName] = new List<Action<object>>();
            _subscriptions[typeName].Add(callback);
        }
    }

    public string GetToolsRoutingText(string query = "", int maxTools = 5)
    {
        List<RegistryTool> tools;
        lock (_lock)
        {
            tools = Tools.Values.Where(t => t.Enabled).ToList();
        }

        if (!string.IsNullOrEmpty(query))
        {
            var words = query.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            tools = tools
                .Select(t => (tool: t, score: words.Sum(w =>
                    (t.Name + t.Description).ToLower().Contains(w) ? 1 : 0)))
                .OrderByDescending(x => x.score)
                .Take(maxTools)
                .Select(x => x.tool)
                .ToList();
        }

        var lines = new List<string>();
        foreach (var t in tools.Take(maxTools))
        {
            var formula = string.IsNullOrEmpty(t.Formula) ? "" : $"\n  公式: {t.Formula}";
            lines.Add($"- **{t.Name}** [{t.Category}]: {t.Description}{formula}");
        }

        return string.Join("\n", lines);
    }

    public Dictionary<string, object> GetStatus()
    {
        return new Dictionary<string, object>
        {
            ["tools"] = Tools.Count,
            ["skills"] = Skills.Count,
            ["roles"] = Roles.Count
        };
    }

    public void BuildDefault()
    {
        var defaultTools = new[]
        {
            new RegistryTool { Name = "code_gen", Description = "Generate code from requirements", Category = "code", Source = "hardcoded" },
            new RegistryTool { Name = "code_review", Description = "Review code for quality issues", Category = "code", Source = "hardcoded" },
            new RegistryTool { Name = "knowledge_search", Description = "Search knowledge base", Category = "knowledge", Source = "hardcoded" },
            new RegistryTool { Name = "web_fetch", Description = "Fetch web content", Category = "web", Source = "hardcoded" },
            new RegistryTool { Name = "doc_parse", Description = "Parse document content", Category = "document", Source = "hardcoded" },
            new RegistryTool { Name = "reasoning", Description = "Logical reasoning and analysis", Category = "reasoning", Source = "hardcoded" },
            new RegistryTool { Name = "text_analyze", Description = "Analyze text content", Category = "text", Source = "hardcoded" }
        };

        var defaultRoles = new[]
        {
            new RegistryRole { Name = "evolver", Description = "Generates creative solutions and plans", Source = "hardcoded" },
            new RegistryRole { Name = "evaluator", Description = "Evaluates and scores solutions", Source = "hardcoded" },
            new RegistryRole { Name = "verifier", Description = "Verifies correctness and completeness", Source = "hardcoded" },
            new RegistryRole { Name = "researcher", Description = "Researches and gathers information", Source = "hardcoded" }
        };

        foreach (var t in defaultTools)
            RegisterTool(t);
        foreach (var r in defaultRoles)
            RegisterRole(r);
    }

    private void Notify(string type, object item)
    {
        if (!_subscriptions.TryGetValue(type, out var callbacks)) return;
        foreach (var cb in callbacks)
        {
            try { cb(item); } catch { }
        }
    }
}
