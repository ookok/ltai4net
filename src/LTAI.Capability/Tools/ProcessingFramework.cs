namespace LTAI.Capability.Tools;

public sealed class ProcessingToolSpec
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "general";
    public List<Dictionary<string, object>> Inputs { get; set; } = new();
    public List<Dictionary<string, object>> Outputs { get; set; } = new();
    public Dictionary<string, object> Parameters { get; set; } = new();
    public string Provider { get; set; } = "livingtree";
    public string Icon { get; set; } = "🔧";
    public List<string> Tags { get; set; } = new();
    public bool Chainable { get; set; } = true;
    public string InputSource { get; set; } = "";
}

public sealed class ChainStep
{
    public ProcessingToolSpec Tool { get; set; } = new();
    public Dictionary<string, string> InputMap { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public int Order { get; set; }
}

public sealed class ToolChain
{
    public string Name { get; set; } = "";
    public List<ChainStep> Steps { get; set; } = new();
    public string Description { get; set; } = "";
    public int TotalTools => Steps.Count;

    public Dictionary<string, object> Validate()
    {
        var issues = new List<Dictionary<string, object>>();
        for (var i = 1; i < Steps.Count; i++)
        {
            var prev = Steps[i - 1];
            var step = Steps[i];
            var prevOutputs = prev.Tool.Outputs.ToDictionary(
                o => o.GetValueOrDefault("name", "")?.ToString() ?? "",
                o => o.GetValueOrDefault("type", "")?.ToString() ?? "");

            foreach (var inp in step.Tool.Inputs)
            {
                var inpName = inp.GetValueOrDefault("name", "")?.ToString() ?? "";
                var inpType = inp.GetValueOrDefault("type", "")?.ToString() ?? "";
                if (step.InputMap.TryGetValue(inpName, out var src) &&
                    prevOutputs.TryGetValue(src, out var srcType) &&
                    srcType != inpType && inpType != "any")
                {
                    issues.Add(new Dictionary<string, object>
                    {
                        ["step"] = i, ["param"] = inpName,
                        ["expected"] = inpType, ["actual"] = srcType
                    });
                }
            }
        }

        return new Dictionary<string, object>
        {
            ["valid"] = issues.Count == 0,
            ["issues"] = issues
        };
    }
}

public sealed class ProcessingFramework
{
    private static readonly Lazy<ProcessingFramework> _instance = new(() => new ProcessingFramework());
    public static ProcessingFramework Instance => _instance.Value;

    private readonly Dictionary<string, ProcessingToolSpec> _tools = new();
    private readonly List<ToolChain> _chains = new();

    private ProcessingFramework()
    {
        RegisterBuiltins();
    }

    private void RegisterBuiltins()
    {
        var builtins = new[]
        {
            new ProcessingToolSpec
            {
                Name = "text_extract", Description = "Extract text", Category = "text",
                Inputs = new() { new() { ["name"] = "content", ["type"] = "string", ["required"] = true } },
                Outputs = new() { new() { ["name"] = "text", ["type"] = "string", ["description"] = "Extracted plain text" } }
            },
            new ProcessingToolSpec
            {
                Name = "llm_chat", Description = "LLM conversation", Category = "llm",
                Inputs = new() { new() { ["name"] = "prompt", ["type"] = "string", ["required"] = true } },
                Outputs = new() { new() { ["name"] = "response", ["type"] = "string", ["description"] = "LLM response" } },
                Parameters = new() { ["temperature"] = 0.7 }
            },
            new ProcessingToolSpec
            {
                Name = "knowledge_search", Description = "Knowledge base search", Category = "knowledge",
                Inputs = new() { new() { ["name"] = "query", ["type"] = "string", ["required"] = true } },
                Outputs = new() { new() { ["name"] = "results", ["type"] = "list", ["description"] = "Search results" } }
            },
            new ProcessingToolSpec
            {
                Name = "web_fetch", Description = "Web page fetch", Category = "web",
                Inputs = new() { new() { ["name"] = "url", ["type"] = "string", ["required"] = true } },
                Outputs = new() { new() { ["name"] = "content", ["type"] = "string", ["description"] = "Page content" } }
            },
            new ProcessingToolSpec
            {
                Name = "report_generate", Description = "Report generation", Category = "output",
                Inputs = new() { new() { ["name"] = "data", ["type"] = "any", ["required"] = true } },
                Outputs = new() { new() { ["name"] = "report", ["type"] = "string", ["description"] = "Generated report" } }
            },
            new ProcessingToolSpec
            {
                Name = "code_generate", Description = "Code generation", Category = "code",
                Inputs = new() { new() { ["name"] = "spec", ["type"] = "string", ["required"] = true } },
                Outputs = new() { new() { ["name"] = "code", ["type"] = "string", ["description"] = "Generated code" } }
            }
        };

        foreach (var t in builtins)
            _tools[t.Name] = t;
    }

    public void Register(ProcessingToolSpec tool) => _tools[tool.Name] = tool;
    public void Unregister(string name) => _tools.Remove(name);
    public ProcessingToolSpec? GetTool(string name) => _tools.GetValueOrDefault(name);

    public List<ProcessingToolSpec> ListTools(string? category = null)
    {
        var tools = _tools.Values.AsEnumerable();
        if (!string.IsNullOrEmpty(category))
            tools = tools.Where(t => t.Category == category);
        return tools.OrderBy(t => t.Name).ToList();
    }

    public ToolChain BuildChain(string name, List<string> toolNames,
        List<Dictionary<string, object>>? connections = null)
    {
        var steps = new List<ChainStep>();
        for (var i = 0; i < toolNames.Count; i++)
        {
            if (!_tools.TryGetValue(toolNames[i], out var tool))
                continue;

            var inputMap = new Dictionary<string, string>();
            if (connections != null)
            {
                foreach (var conn in connections)
                {
                    if (conn.TryGetValue("to_step", out var ts) && ts is int stepIdx && stepIdx == i)
                    {
                        var toInput = conn.GetValueOrDefault("to_input", "")?.ToString() ?? "";
                        var fromOutput = conn.GetValueOrDefault("from_output", "")?.ToString() ?? "";
                        if (toInput.Length > 0 && fromOutput.Length > 0)
                            inputMap[toInput] = fromOutput;
                    }
                }
            }

            steps.Add(new ChainStep { Tool = tool, InputMap = inputMap, Order = i });
        }

        var chain = new ToolChain { Name = name, Steps = steps };
        _chains.Add(chain);
        return chain;
    }

    public ToolChain? GetChain(string name) => _chains.FirstOrDefault(c => c.Name == name);

    public int ToolCount => _tools.Count;
    public int ChainCount => _chains.Count;
}
