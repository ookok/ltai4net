using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tools.Tools;

public record SynthesizedTool(string Name, string Description, string Code, string Category,
    List<string> Params, int Version, int SuccessCount, int FailureCount,
    DateTime CreatedAt, DateTime LastUsed, string SourceTask);

public record SynthesisResult(bool Success, SynthesizedTool? Tool, string? Error);

public sealed class ToolSynthesizer
{
    private readonly string _storeDir;
    private readonly ILogger<ToolSynthesizer> _logger;
    private readonly object _lock = new();
    private Dictionary<string, SynthesizedTool> _registry = new();

    public ToolSynthesizer(ILogger<ToolSynthesizer>? logger = null)
    {
        _storeDir = Path.Combine(Directory.GetCurrentDirectory(), ".livingtree", "synthesized_tools");
        Directory.CreateDirectory(_storeDir);
        _logger = logger ?? NullLogger<ToolSynthesizer>.Instance;
        LoadRegistry();
    }

    public async Task<SynthesisResult> Synthesize(string description, string category,
        Func<string, string, Task<string>> chatFn)
    {
        var prompt = $@"You are a code generator. Create a Python function based on this description. Output ONLY valid JSON.

Description: {description}

Return JSON with: name, code (Python), params (list of param names).

The code must contain a function called 'execute' that takes params as arguments and returns a dict.";
        try
        {
            var response = await chatFn("tool_synthesis", prompt);
            var json = ExtractJson(response);
            if (json == null) return new SynthesisResult(false, null, "Invalid JSON response");

            var name = json.Value.GetProperty("name").GetString() ?? "unnamed_tool";
            var code = json.Value.GetProperty("code").GetString() ?? "";
            var paramList = json.Value.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                : new List<string>();

            var tool = new SynthesizedTool(name, description, code, category, paramList, 1, 0, 0,
                DateTime.UtcNow, DateTime.UtcNow, description);
            SaveTool(tool);
            lock (_lock) { _registry[name] = tool; }

            _logger.LogInformation("Synthesized tool: {Name}", name);
            return new SynthesisResult(true, tool, null);
        }
        catch (Exception ex)
        {
            return new SynthesisResult(false, null, ex.Message);
        }
    }

    public SynthesizedTool? GetTool(string name)
    {
        lock (_lock) { return _registry.GetValueOrDefault(name); }
    }

    public List<SynthesizedTool> ListTools()
    {
        lock (_lock) { return _registry.Values.OrderBy(t => t.Name).ToList(); }
    }

    private void SaveTool(SynthesizedTool tool)
    {
        var path = Path.Combine(_storeDir, $"{SantizeFileName(tool.Name)}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(tool, new JsonSerializerOptions { WriteIndented = true }));
        SaveRegistry();
    }

    private void LoadRegistry()
    {
        var registryPath = Path.Combine(_storeDir, "registry.json");
        if (!File.Exists(registryPath)) return;
        try
        {
            var json = File.ReadAllText(registryPath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, SynthesizedTool>>(json);
            lock (_lock) { if (loaded != null) _registry = loaded; }
        }
        catch { /* non-fatal */ }
    }

    private void SaveRegistry()
    {
        lock (_lock)
        {
            File.WriteAllText(Path.Combine(_storeDir, "registry.json"),
                JsonSerializer.Serialize(_registry, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static JsonElement? ExtractJson(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```")) text = text.Substring(text.IndexOf('\n') + 1);
        if (text.EndsWith("```")) text = text.Substring(0, text.LastIndexOf("```"));
        try { return JsonSerializer.Deserialize<JsonElement>(text); }
        catch { return null; }
    }

    private static string SantizeFileName(string name)
    {
        return string.Join("_", name.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_").ToLowerInvariant();
    }
}
