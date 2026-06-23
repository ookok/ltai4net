using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LTAI.Agent.Tools;
using Microsoft.Extensions.AI;

namespace LTAI.Agent;

/// <summary>
/// Central tool registry that maps agent names to their <see cref="AITool"/> lists.
///
/// Aligns with MAF's keyed <c>AITool</c> DI pattern: tools are stored per-agent
/// and can be discovered by external consumers (DevUI, other agents, workflows)
/// without requiring the tools to be in <see cref="ChatOptions.Tools"/> at all times.
///
/// Registration happens during <see cref="AgentBuilder.BuildAgentImpl"/> after
/// all tools are assembled. Resolution is O(1) per agent name.
/// </summary>
public sealed class AgentToolStore
{
    private readonly ConcurrentDictionary<string, List<AITool>> _store = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a tool for the specified agent. Idempotent per tool name
    /// (last registration wins per agent).
    /// </summary>
    public void Register(string agentName, AITool tool)
    {
        var list = _store.GetOrAdd(agentName, _ => new List<AITool>());
        lock (list)
        {
            var idx = list.FindIndex(t => string.Equals(t.Name, tool.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                list[idx] = tool;
            else
                list.Add(tool);
        }
    }

    /// <summary>
    /// Registers multiple tools for the specified agent.
    /// </summary>
    public void RegisterRange(string agentName, IEnumerable<AITool> tools)
    {
        foreach (var tool in tools)
            Register(agentName, tool);
    }

    /// <summary>
    /// Registers multiple tools for an agent, clearing any existing tools first.
    /// </summary>
    public void RegisterToolsForAgent(string agentName, IEnumerable<AITool> tools)
    {
        var list = _store.GetOrAdd(agentName, _ => new List<AITool>());
        lock (list)
        {
            list.Clear();
            list.AddRange(tools);
        }
    }

    /// <summary>
    /// Returns the tool list for the specified agent, or an empty list if none registered.
    /// </summary>
    public IReadOnlyList<AITool> GetTools(string agentName)
    {
        if (_store.TryGetValue(agentName, out var list))
        {
            lock (list)
                return list.ToArray();
        }
        return Array.Empty<AITool>();
    }

    /// <summary>
    /// Returns the tool list for the specified agent. Alias for <see cref="GetTools"/>.
    /// </summary>
    public IReadOnlyList<AITool> GetAgentTools(string agentName) => GetTools(agentName);

    /// <summary>
    /// Returns all agent names that have registered tools.
    /// </summary>
    public IEnumerable<string> GetAgentNames() => _store.Keys;

    /// <summary>
    /// Removes a tool by name from an agent's tool set. Returns true if found and removed.
    /// </summary>
    public bool RemoveTool(string agentName, string toolName)
    {
        if (!_store.TryGetValue(agentName, out var list)) return false;
        lock (list)
        {
            var idx = list.FindIndex(t => string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            list.RemoveAt(idx);
            return true;
        }
    }

    /// <summary>Returns all tool names for an agent.</summary>
    public string[] GetToolNames(string agentName)
    {
        if (!_store.TryGetValue(agentName, out var list)) return [];
        lock (list)
            return list.Select(t => t.Name ?? "").Where(n => n.Length > 0).ToArray();
    }

    /// <summary>
    /// Returns the number of tools registered for the specified agent.
    /// </summary>
    public int GetToolCount(string agentName)
        => _store.TryGetValue(agentName, out var list) ? list.Count : 0;

    /// <summary>
    /// Hot-reloads tools for the specified agent from <c>.livingtree/tools/{agentName}/</c>.
    /// Scans all <c>*.tool.json</c> files in that directory and registers them as <see cref="AITool"/> instances.
    /// If the directory does not exist or contains no valid tool definitions, the agent's tools are cleared.
    /// </summary>
    private static readonly HashSet<string> AllowedScriptExts = new(StringComparer.OrdinalIgnoreCase)
        { ".ps1", ".cmd", ".bat", ".sh" };

    private const int MaxScriptSizeBytes = 1_000_000;
    private const int ContentScanBytes = 4096;

    /// <summary>
    /// Scans script content for dangerous patterns using <see cref="ShellSecurity.DangerousPatterns"/>.
    /// Returns an error message if dangerous content is detected, or null if safe.
    /// </summary>
    private static string? ScanScriptContent(string scriptPath)
    {
        try
        {
            var fi = new FileInfo(scriptPath);
            if (fi.Length > MaxScriptSizeBytes)
                return $"script exceeds {MaxScriptSizeBytes / 1024}KB size limit";
            if (fi.Length == 0)
                return "script is empty";

            using var stream = fi.OpenRead();
            var buffer = new byte[ContentScanBytes];
            var bytesRead = stream.Read(buffer, 0, ContentScanBytes);
            stream.Close();

            // Reject binary content: null byte in first 512 bytes
            var scanLen = Math.Min(bytesRead, 512);
            for (int i = 0; i < scanLen; i++)
            {
                if (buffer[i] == 0)
                    return "script contains binary content (null byte detected)";
            }

            // Convert to text for pattern matching (first ContentScanBytes)
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var textLower = text.ToLowerInvariant();

            foreach (var pattern in ShellSecurity.DangerousPatterns)
            {
                if (textLower.Contains(pattern.ToLowerInvariant()))
                    return $"script contains dangerous pattern: '{pattern}'";
            }

            // Check for code execution patterns (downloading + executing)
            foreach (var downloadPattern in new[] { "invoke-webrequest", "wget", "curl", "certutil", "bitsadmin" })
            {
                if (textLower.Contains(downloadPattern))
                {
                    // Only block if combined with execution indicators
                    if (textLower.Contains("-exec") || textLower.Contains("|") || textLower.Contains("iex"))
                        return $"script contains download + execute pattern: '{downloadPattern}'";
                }
            }

            // Check for dangerous PowerShell script blocks
            var dangerKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "invoke-expression", "iex ", "invoke-command", "invoke-wmimethod",
                "start-process -windowstyle hidden", "bypass -enc", "-enc ",
                "frombase64string", "byte[]", "memorystream",
                "add-type", "unsafeinterop", "dllimport",
            };
            foreach (var keyword in dangerKeywords)
            {
                if (textLower.Contains(keyword))
                    return $"script blocked: contains '{keyword}'";
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"cannot scan script content: {ex.Message}";
        }
    }

    public void HotReloadAgentTools(string agentName)
    {
        var livingTreeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".livingtree"));
        var toolsDir = Path.Combine(livingTreeRoot, "tools", agentName);
        var tools = new List<AITool>();

        if (Directory.Exists(toolsDir))
        {
            foreach (var file in Directory.GetFiles(toolsDir, "*.tool.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var def = JsonSerializer.Deserialize<ToolFileDefinition>(json);
                    if (def == null || string.IsNullOrEmpty(def.Name) || string.IsNullOrEmpty(def.ScriptPath))
                        continue;

                    // Security: ScriptPath must be a relative path inside .livingtree/tools/
                    var scriptFull = Path.GetFullPath(Path.Combine(toolsDir, def.ScriptPath));
                    if (!scriptFull.StartsWith(livingTreeRoot, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var ext = Path.GetExtension(scriptFull);
                    if (!AllowedScriptExts.Contains(ext))
                        continue;
                    if (scriptFull.Length > 260)
                        continue;
                    if (!File.Exists(scriptFull))
                        continue;
                    if (ShellSecurity.IsBlocked(scriptFull))
                        continue;

                    // Content security scan
                    var scanError = ScanScriptContent(scriptFull);
                    if (scanError != null)
                        continue;

                    Func<string> executeScript = () =>
                    {
                        try
                        {
                            var startInfo = new ProcessStartInfo
                            {
                                FileName = scriptFull,
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                            };
                            ShellSecurity.RestrictEnvironment(startInfo,
                                OperatingSystem.IsWindows(), "");
                            using var proc = new Process { StartInfo = startInfo };
                            proc.Start();
                            var outText = proc.StandardOutput.ReadToEnd();
                            var errText = proc.StandardError.ReadToEnd();
                            proc.WaitForExit(30000);
                            return (outText + errText).Trim();
                        }
                        catch (Exception ex)
                        {
                            return $"脚本执行失败: {ex.Message}";
                        }
                    };
                    var tool = AIFunctionFactory.Create(
                        executeScript,
                        def.Name,
                        def.Description);
                    tools.Add(tool);
                }
                catch
                {
                    // skip malformed tool files
                }
            }
        }

        RegisterToolsForAgent(agentName, tools);
    }

    private sealed class ToolFileDefinition
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? ScriptPath { get; set; }
        public string[]? Parameters { get; set; }
    }
}
