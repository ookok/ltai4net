using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LTAI.Planning.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Planning.Quality;

public sealed class AutoSkillResolver
{
    private static readonly Lazy<AutoSkillResolver> _instance = new(() => new AutoSkillResolver());
    public static AutoSkillResolver Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, ResolvedSkill> _registry = new();
    private readonly string _basePath = ".livingtree/auto_skills";
    private readonly ILogger<AutoSkillResolver> _logger;

    private AutoSkillResolver()
    {
        _logger = NullLogger<AutoSkillResolver>.Instance;

        if (!Directory.Exists(_basePath))
            Directory.CreateDirectory(_basePath);
    }

    internal AutoSkillResolver(ILogger<AutoSkillResolver> logger)
    {
        _logger = logger ?? NullLogger<AutoSkillResolver>.Instance;

        if (!Directory.Exists(_basePath))
            Directory.CreateDirectory(_basePath);
    }

    public static readonly HashSet<string> DangerousImports = new(StringComparer.OrdinalIgnoreCase)
    {
        "subprocess", "os.system", "shutil.rmtree", "eval", "exec",
        "__import__", "compile", @"open(.*""w""", "socket"
    };

    public static readonly HashSet<string> DangerousCalls = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm -rf", "del /f", "format c:", "shutdown", "reboot",
        "chmod 777", @"curl.*\|.*sh"
    };

    public List<string> DetectMissing(string agentOutput, string taskDesc)
    {
        var skills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var patterns = new List<string>
        {
            @"skill.*not found:\s*(\w+)",
            @"tool.*not available:\s*(\w+)",
            @"需要.*技能[：:]\s*(\w+)",
            @"missing.*library[：:]\s*(\w+)",
            @"pip install (\w+)"
        };

        var input = string.IsNullOrEmpty(taskDesc) ? agentOutput : agentOutput + "\n" + taskDesc;

        foreach (var pattern in patterns)
        {
            var matches = Regex.Matches(input, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            foreach (Match match in matches)
            {
                var name = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(name))
                    skills.Add(name);
            }
        }

        _logger.LogDebug("Detected {Count} missing skills from agent output", skills.Count);
        return skills.ToList();
    }

    public async Task<ResolvedSkill?> Resolve(
        string skillName,
        string taskContext,
        Func<string, string?, CancellationToken, Task<string?>>? llmCall = null)
    {
        if (_registry.TryGetValue(skillName, out var cached))
        {
            _logger.LogDebug("Skill {Skill} found in registry", skillName);
            return cached;
        }

        ResolvedSkill? result = null;

        if (llmCall != null)
        {
            result = await GenerateCode(skillName, taskContext, llmCall);
        }

        if (result == null)
        {
            result = new ResolvedSkill
            {
                Name = skillName,
                Type = "unresolved",
                Description = $"Placeholder for unresolved skill: {skillName}",
                Source = "auto_skill_resolver",
                Handler = "",
                CreatedAt = DateTime.UtcNow,
                UsedCount = 0
            };

            _logger.LogWarning("Skill {Skill} recorded as unresolved placeholder", skillName);
        }

        _registry.TryAdd(skillName, result);
        return result;
    }

    public bool ScanCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var lines = code.Split('\n', StringSplitOptions.None);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            foreach (var dangerous in DangerousImports)
            {
                if (dangerous.StartsWith("open"))
                {
                    if (Regex.IsMatch(trimmed, dangerous, RegexOptions.IgnoreCase))
                    {
                        _logger.LogWarning("Dangerous import detected: {Import} in line: {Line}", dangerous, trimmed);
                        return false;
                    }
                }
                else
                {
                    if (trimmed.Contains(dangerous, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Dangerous import detected: {Import} in line: {Line}", dangerous, trimmed);
                        return false;
                    }
                }
            }

            foreach (var dangerous in DangerousCalls)
            {
                if (dangerous.Contains('*') || dangerous.Contains('|'))
                {
                    if (Regex.IsMatch(trimmed, dangerous, RegexOptions.IgnoreCase))
                    {
                        _logger.LogWarning("Dangerous call detected: {Call} in line: {Line}", dangerous, trimmed);
                        return false;
                    }
                }
                else
                {
                    if (trimmed.Contains(dangerous, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Dangerous call detected: {Call} in line: {Line}", dangerous, trimmed);
                        return false;
                    }
                }
            }
        }

        return true;
    }

    public async Task<ResolvedSkill?> GenerateCode(
        string name,
        string context,
        Func<string, string?, CancellationToken, Task<string?>>? llmCall)
    {
        if (llmCall == null)
            return null;

        var prompt = $@"You are a code generation assistant. Generate a C# helper skill named '{name}'.

Context: {context}

Requirements:
- The code must be a valid C# class or script.
- Use only safe, standard .NET libraries.
- Do NOT include any file system operations, network calls, or process execution unless clearly needed.
- Return ONLY the code, no explanations.

Generate the code for the skill '{name}':";

        string? generatedCode = null;
        try
        {
            generatedCode = await llmCall(prompt, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM call failed for skill generation: {Skill}", name);
            return null;
        }

        if (string.IsNullOrWhiteSpace(generatedCode))
        {
            _logger.LogWarning("LLM returned empty code for skill: {Skill}", name);
            return null;
        }

        if (!ScanCode(generatedCode))
        {
            _logger.LogWarning("Generated code for skill {Skill} failed security scan", name);
            return null;
        }

        var ext = generatedCode.Contains("class ") || generatedCode.Contains("namespace ") ? ".cs" : ".csx";
        var filePath = Path.Combine(_basePath, $"{SanitizeFileName(name)}{ext}");

        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(filePath, generatedCode);
            _logger.LogInformation("Skill code written to {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write skill code to {Path}", filePath);
            return null;
        }

        var skill = new ResolvedSkill
        {
            Name = name,
            Type = "auto_generated",
            Description = $"Auto-generated skill for: {name}. Context: {context}",
            Source = filePath,
            Handler = ext == ".cs" ? "compile" : "script",
            CreatedAt = DateTime.UtcNow,
            UsedCount = 1
        };

        return skill;
    }

    public async Task RetryWithResolved(
        Func<Task<bool>> stepFunc,
        string taskContext,
        int maxAttempts = 3)
    {
        var attempt = 0;

        while (attempt < maxAttempts)
        {
            attempt++;
            try
            {
                var success = await stepFunc();
                if (success)
                    return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Step attempt {Attempt} failed", attempt);

                var missingSkills = DetectMissing(ex.Message, taskContext);
                if (missingSkills.Count == 0)
                {
                    if (attempt >= maxAttempts)
                        throw;
                    continue;
                }

                foreach (var skillName in missingSkills)
                {
                    _logger.LogInformation("Attempting to resolve missing skill: {Skill}", skillName);
                    var resolved = await Resolve(skillName, taskContext);
                    if (resolved != null)
                    {
                        _logger.LogInformation("Resolved skill {Skill} (type={Type})", skillName, resolved.Type);
                    }
                }
            }

            if (attempt >= maxAttempts)
            {
                throw new InvalidOperationException(
                    $"Failed to complete step after {maxAttempts} attempts. Last attempt: {attempt}");
            }
        }
    }

    public Dictionary<string, object?> GetStats()
    {
        var resolvedSummaries = new Dictionary<string, object?>();
        foreach (var kvp in _registry)
        {
            resolvedSummaries[kvp.Key] = new Dictionary<string, object?>
            {
                ["Name"] = kvp.Value.Name,
                ["Type"] = kvp.Value.Type,
                ["Source"] = kvp.Value.Source,
                ["CreatedAt"] = kvp.Value.CreatedAt,
                ["UsedCount"] = kvp.Value.UsedCount
            };
        }

        return new Dictionary<string, object?>
        {
            ["RegistryCount"] = _registry.Count,
            ["ResolvedSkills"] = resolvedSummaries
        };
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new char[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            sanitized[i] = invalid.Contains(name[i]) ? '_' : name[i];
        }
        return new string(sanitized);
    }
}
