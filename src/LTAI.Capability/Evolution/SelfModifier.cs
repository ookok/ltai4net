using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Capability.Evolution;

public record ModifyResult(string Task, List<string> FilesChanged, string DiffSummary,
    bool Success, bool RolledBack, string? Error);

public sealed class SelfModifier
{
    private readonly string _workspace;
    private readonly ILogger<SelfModifier> _logger;

    public SelfModifier(string? workspace = null, ILogger<SelfModifier>? logger = null)
    {
        _workspace = workspace ?? Directory.GetCurrentDirectory();
        _logger = logger ?? NullLogger<SelfModifier>.Instance;
    }

    public async Task<ModifyResult> ModifyAsync(string task, Func<string, string, Task<string>> chatFn)
    {
        try
        {
            var files = ScanProject();
            var fileList = string.Join("\n", files.Take(50).Select(f => $"{f.Path} ({f.Lines} lines)"));
            var analysis = await chatFn("modify_analyze",
                $"Task: {task}\n\nProject files:\n{fileList}\n\nReturn JSON with 'files_to_modify' array of file paths and 'changes' description.");
            var targets = ParseFileList(analysis);

            var changes = new List<string>();
            foreach (var target in targets.Take(5))
            {
                if (!File.Exists(target)) continue;
                var content = File.ReadAllText(target);
                var modified = await chatFn($"modify_{Path.GetFileName(target)}",
                    $"Task: {task}\nFile: {Path.GetRelativePath(_workspace, target)}\n\nOriginal:\n{content[..Math.Min(content.Length, 15000)]}\n\nReturn JSON with 'modified_code' and 'summary'.");
                var (newCode, summary) = ParseModification(modified, content);
                if (!string.IsNullOrEmpty(newCode) && newCode != content)
                {
                    File.WriteAllText(target, newCode);
                    changes.Add($"{target}: {summary}");
                }
            }

            return new ModifyResult(task, changes, string.Join("\n", changes),
                changes.Count > 0, false, null);
        }
        catch (Exception ex)
        {
            return new ModifyResult(task, new(), "", false, true, ex.Message);
        }
    }

    private List<(string Path, int Lines)> ScanProject()
    {
        var files = new List<(string, int)>();
        foreach (var file in Directory.GetFiles(_workspace, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains("\\obj\\") || file.Contains("\\bin\\")) continue;
            try
            {
                files.Add((file, File.ReadAllLines(file).Length));
            }
            catch { }
        }
        return files.OrderByDescending(f => f.Item2).ToList();
    }

    private static List<string> ParseFileList(string llmOutput)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(ExtractJson(llmOutput));
            if (json.TryGetProperty("files_to_modify", out var arr))
                return arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
        }
        catch { }
        return Regex.Matches(llmOutput, @"[\w/\\-]+\.\w+").Select(m => m.Value).Distinct().ToList();
    }

    private static (string Code, string Summary) ParseModification(string llmOutput, string original)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(ExtractJson(llmOutput));
            return (json.TryGetProperty("modified_code", out var c) ? c.GetString() ?? original : original,
                    json.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "");
        }
        catch
        {
            var match = Regex.Match(llmOutput, @"```\w*\n(.*?)```", RegexOptions.Singleline);
            return match.Success ? (match.Groups[1].Value, "code block") : (original, "");
        }
    }

    private static string ExtractJson(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```")) text = text[(text.IndexOf('\n') + 1)..];
        if (text.EndsWith("```")) text = text[..text.LastIndexOf("```")];
        return text;
    }
}
