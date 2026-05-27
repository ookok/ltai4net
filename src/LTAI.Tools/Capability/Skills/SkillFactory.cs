using System.Diagnostics;
using System.Text.Json;
using LTAI.Core.Governors;
using Microsoft.Extensions.Logging;

namespace LTAI.Tools.Skills;

public record SkillSpec(string Name, string Description, Dictionary<string, object> InputSchema,
    Dictionary<string, object> OutputSchema, string Category, string Code, List<string> TestCases);

public sealed class SkillFactory
{
    private readonly Dictionary<string, SkillSpec> _skills = new();
    private readonly Dictionary<string, object> _instances = new();
    private readonly ILogger<SkillFactory> _logger;
    private readonly object _lock = new();

    public SkillFactory(ILogger<SkillFactory>? logger = null)
    {
        _logger = logger ?? NullLogger<SkillFactory>.Instance;
    }

    public void DiscoverSkills()
    {
        var skillsDir = Path.Combine(Environment.GetEnvironmentVariable("LTAI_WORKSPACE") ?? Directory.GetCurrentDirectory(), ".livingtree", "skills");
        if (!Directory.Exists(skillsDir)) return;

        foreach (var file in Directory.GetFiles(skillsDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var spec = JsonSerializer.Deserialize<SkillSpec>(json);
                if (spec != null) Register(spec);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load skill: {File}", file);
            }
        }
    }

    public SkillSpec CreateSkill(string name, string description, string category, string code, List<string>? testCases = null)
    {
        var spec = new SkillSpec(name, description,
            new Dictionary<string, object> { ["type"] = "object" },
            new Dictionary<string, object> { ["type"] = "object" },
            category, code, testCases ?? new List<string>());
        Register(spec);
        return spec;
    }

    public SkillSpec? ComposeSkills(string name, List<string> skillNames)
    {
        if (skillNames.Count > 5) return null;

        var selected = new List<SkillSpec>();
        foreach (var sn in skillNames)
        {
            if (_skills.TryGetValue(sn, out var s)) selected.Add(s);
        }

        if (selected.Count == 0) return null;

        var codeParts = new List<string>();
        codeParts.Add("import json\nimport sys\n");
        for (int i = 0; i < selected.Count; i++)
        {
            codeParts.Add($"# Stage {i + 1}: {selected[i].Name}");
            codeParts.Add(selected[i].Code);
        }

        var composed = new SkillSpec(name, $"Composed skill: {string.Join(" -> ", skillNames)}",
            new Dictionary<string, object> { ["type"] = "object" },
            new Dictionary<string, object> { ["type"] = "object" },
            "composed", string.Join('\n', codeParts),
            new List<string>());

        Register(composed);
        return composed;
    }

    public void Register(SkillSpec spec)
    {
        lock (_lock) { _skills[spec.Name] = spec; }
    }

    public SkillSpec? GetSkill(string name)
    {
        lock (_lock) { return _skills.GetValueOrDefault(name); }
    }

    public List<SkillSpec> ListByCategory(string? category = null)
    {
        lock (_lock)
        {
            var skills = _skills.Values.ToList();
            if (category != null) skills = skills.Where(s => s.Category == category).ToList();
            return skills;
        }
    }

    public async Task<(bool Success, string Output)> TestSkill(string name)
    {
        var spec = GetSkill(name);
        if (spec == null) return (false, "Skill not found");

        return await ExecuteInIsolationAsync(spec, "{}");
    }

    private static async Task<(bool Success, string Output)> ExecuteInIsolationAsync(SkillSpec spec, string inputData)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"ltai_skill_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tmpDir);
            var codeFile = Path.Combine(tmpDir, "skill_exec.py");
            var code = $@"
import json
import sys

input_data = json.loads(sys.argv[1]) if len(sys.argv) > 1 else {{}}

{spec.Code}

result = execute(input_data) if 'execute' in dir() else {{'error': 'no execute function'}}
print(json.dumps(result, default=str))
";
            File.WriteAllText(codeFile, code);

            if (MicroKernel.Default != null)
            {
                var kResult = await MicroKernel.Default.ExecuteAsync(new KernelOp
                {
                    Command = "python",
                    Arguments = $"\"{codeFile}\" \"{inputData}\"",
                    WorkingDirectory = tmpDir,
                    Timeout = TimeSpan.FromSeconds(30)
                }).ConfigureAwait(false);

                return (kResult.Success, kResult.Data.Length > 0 ? kResult.Data : kResult.Error);
            }

            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{codeFile}\" \"{inputData}\"",
                WorkingDirectory = tmpDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (false, "Failed to start process");

            proc.WaitForExit(TimeSpan.FromSeconds(30));
            if (!proc.HasExited) { proc.Kill(); return (false, "Timeout"); }

            var output = proc.StandardOutput.ReadToEnd().Trim();
            var error = proc.StandardError.ReadToEnd().Trim();
            return (proc.ExitCode == 0 && !string.IsNullOrEmpty(output), output.Length > 0 ? output : error);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { /* non-fatal */ }
        }
    }
}
