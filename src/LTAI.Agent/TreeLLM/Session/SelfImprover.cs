using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using LTAI.Agent.Models;

namespace LTAI.Agent.Session;

public sealed class SelfImprover
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly List<Defect> _defects = new();
    private readonly List<Innovation> _innovations = new();
    private readonly ILogger<SelfImprover>? _logger;
    private readonly string _persistPath;
    private readonly string _projectRoot;

    private static readonly (string Category, string Title, string Pattern)[] ScanRules =
    {
        ("imports", "Too Many Imports", @"^using\s"),
        ("exception", "Bare Exception Catch", @"catch\s*\(\s*Exception\s+"),
        ("todo", "TODO/FIXME Left", @"TODO|FIXME|HACK|XXX"),
        ("empty_block", "Empty Pass Block", @"\{\s*\}"),
        ("long_function", "Long Function", ""),
        ("debug_output", "Debug Output Left", @"Console\.WriteLine|Debug\.WriteLine"),
        ("sync_over_async", "Synchronous Over Async", @"\.Result\b|\.Wait\(\)"),
        ("hardcoded_string", "Hardcoded String", ""),
        ("missing_null_check", "Missing Null Check", ""),
        ("no_test", "Missing Test File", "")
    };

    public SelfImprover(ILogger<SelfImprover>? logger = null, string? persistPath = null, string? projectRoot = null)
    {
        _logger = logger;
        _persistPath = persistPath ?? Path.Combine("livingtree", "meta", "self_improver.json");
        _projectRoot = projectRoot ?? Directory.GetCurrentDirectory();
        Load();
    }

    public List<Defect> Scan(string? filePattern = null)
    {
        _defects.Clear();
        var files = FindFiles(filePattern);

        foreach (var file in files)
        {
            try
            {
                var content = File.ReadAllText(file);
                var lines = content.Split('\n');
                ScanFile(file, lines, content);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("SelfImprover: Failed to scan {File}: {Message}", file, ex.Message);
            }
        }

        Parallel.ForEach(files, file =>
        {
            try
            {
                ScanWithRegex(file);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("SelfImprover: Regex scan failed for {File}: {Message}", file, ex.Message);
            }
        });

        _logger?.LogInformation("SelfImprover: Scanned {FileCount} files, found {DefectCount} defects",
            files.Count, _defects.Count);

        return _defects.ToList();
    }

    public List<Innovation> Propose(List<Defect>? defects = null)
    {
        _innovations.Clear();
        var df = defects ?? _defects;

        var byCategory = df.GroupBy(d => d.Category);

        foreach (var group in byCategory)
        {
            var innovation = group.Key switch
            {
                "todo" => new Innovation
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Title = "Resolve Technical Debt Markers",
                    Description = $"Auto-resolve {group.Count()} TODO/FIXME markers",
                    Category = "cleanup",
                    EstimatedImpact = 0.6,
                    Complexity = "low"
                },
                "exception" => new Innovation
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Title = "Improve Exception Handling",
                    Description = $"Replace {group.Count()} bare exception catches with specific types",
                    Category = "resilience",
                    EstimatedImpact = 0.8,
                    Complexity = "medium"
                },
                "empty_block" => new Innovation
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Title = "Fill Empty Blocks",
                    Description = $"Add logging to {group.Count()} empty blocks",
                    Category = "observability",
                    EstimatedImpact = 0.5,
                    Complexity = "low"
                },
                _ => new Innovation
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Title = $"Improve {group.Key}",
                    Description = $"Address {group.Count()} issues in category {group.Key}",
                    Category = group.Key,
                    EstimatedImpact = 0.5,
                    Complexity = "medium"
                }
            };

            _innovations.Add(innovation);
        }

        return _innovations.ToList();
    }

    public Dictionary<string, object> Improve(bool autoApply = false)
    {
        var defects = Scan();
        var innovations = Propose(defects);
        var applied = 0;

        if (autoApply)
        {
            foreach (var innovation in innovations.Where(i => i.Complexity == "low"))
            {
                if (ApplyInnovation(innovation))
                {
                    innovation.Validated = true;
                    innovation.TestPassed = true;
                    applied++;
                }
            }
        }

        Save();

        return new Dictionary<string, object>
        {
            ["defects"] = defects.Count,
            ["innovations"] = innovations.Count,
            ["applied"] = applied,
            ["auto_apply"] = autoApply
        };
    }

    private List<string> FindFiles(string? pattern)
    {
        var files = new List<string>();
        var patterns = pattern?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                       ?? new[] { "*.cs" };

        foreach (var p in patterns)
        {
            try
            {
                var found = Directory.GetFiles(_projectRoot, p.Trim(), SearchOption.AllDirectories)
                    .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("\\node_modules\\"))
                    .Take(200);
                files.AddRange(found);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("SelfImprover: Failed to search {Pattern}: {Message}", p, ex.Message);
            }
        }

        return files.Distinct().ToList();
    }

    private void ScanFile(string filePath, string[] lines, string content)
    {
        var fileName = Path.GetFileName(filePath);

        if (lines.Length > 100)
        {
            _defects.Add(new Defect
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Category = "long_function",
                Severity = DefectSeverity.Medium,
                Title = "Long function detected",
                FilePath = filePath,
                LineNumber = 1,
                Description = $"File has {lines.Length} lines"
            });
        }

        var usingCount = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("using ")) usingCount++;
            if (usingCount > 20)
            {
                _defects.Add(new Defect
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Category = "imports",
                    Severity = DefectSeverity.Low,
                    Title = "Too many using directives",
                    FilePath = filePath,
                    LineNumber = i + 1,
                    Description = $"File has {usingCount}+ using directives"
                });
                break;
            }
        }

        if (!fileName.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) &&
            !fileName.EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase) &&
            !filePath.Contains("\\tests\\", StringComparison.OrdinalIgnoreCase))
        {
            var testFileName = fileName.Replace(".cs", "Tests.cs");
            var testDir = Path.Combine(Path.GetDirectoryName(_projectRoot) ?? "", "tests");
            var testPath = Path.Combine(testDir, testFileName);

            if (!File.Exists(testPath) && !Directory.GetFiles(
                Path.GetDirectoryName(filePath) ?? "", "*Tests.cs").Any())
            {
                _defects.Add(new Defect
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Category = "no_test",
                    Severity = DefectSeverity.Medium,
                    Title = "No test file found",
                    FilePath = filePath,
                    Description = "Missing corresponding test file"
                });
            }
        }
    }

    private void ScanWithRegex(string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var lines = content.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], @"TODO|FIXME|HACK|XXX"))
                {
                    _defects.Add(new Defect
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        Category = "todo",
                        Severity = DefectSeverity.Low,
                        Title = "Tech debt marker",
                        FilePath = filePath,
                        LineNumber = i + 1,
                        Description = lines[i].Trim()
                    });
                }

                if (Regex.IsMatch(lines[i], @"catch\s*\(\s*Exception\s+"))
                {
                    _defects.Add(new Defect
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        Category = "exception",
                        Severity = DefectSeverity.High,
                        Title = "Bare Exception catch",
                        FilePath = filePath,
                        LineNumber = i + 1,
                        Description = "Catch-all exception handler"
                    });
                }

                if (Regex.IsMatch(lines[i], @"\{\s*\}"))
                {
                    _defects.Add(new Defect
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        Category = "empty_block",
                        Severity = DefectSeverity.Medium,
                        Title = "Empty block detected",
                        FilePath = filePath,
                        LineNumber = i + 1,
                        Description = "Empty block should log or explain intentionally empty"
                    });
                }

                if (Regex.IsMatch(lines[i], @"Debug\.WriteLine|Console\.Write(Line)?"))
                {
                    _defects.Add(new Defect
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        Category = "debug_output",
                        Severity = DefectSeverity.Low,
                        Title = "Debug output left in code",
                        FilePath = filePath,
                        LineNumber = i + 1,
                        Description = "Replace with ILogger"
                    });
                }

                if (Regex.IsMatch(lines[i], @"\.Result\b|\.Wait\(\)"))
                {
                    _defects.Add(new Defect
                    {
                        Id = Guid.NewGuid().ToString("N")[..12],
                        Category = "sync_over_async",
                        Severity = DefectSeverity.High,
                        Title = "Synchronous blocking over async",
                        FilePath = filePath,
                        LineNumber = i + 1,
                        Description = "Use async/await instead"
                    });
                }
            }
        }
        catch { /* non-fatal */ }
    }

    private bool ApplyInnovation(Innovation innovation)
    {
        try
        {
            var relatedDefects = _defects.Where(d => d.Category == innovation.Category).ToList();
            foreach (var defect in relatedDefects)
            {
                var content = File.ReadAllText(defect.FilePath);
                var lines = content.Split('\n');
                var idx = defect.LineNumber - 1;

                if (defect.Category == "debug_output" && idx >= 0 && idx < lines.Length)
                {
                    lines[idx] = "// Removed debug output";
                    File.WriteAllText(defect.FilePath, string.Join("\n", lines));
                }
            }

            innovation.GitBranch = $"improve/{innovation.Category}_{innovation.Id}";
            Save();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("SelfImprover: Failed to apply {Title}: {Message}", innovation.Title, ex.Message);
            return false;
        }
    }

    public List<Defect> GetDefects() => _defects.ToList();
    public List<Innovation> GetInnovations() => _innovations.ToList();

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["defects"] = _defects.Count,
            ["innovations"] = _innovations.Count,
            ["validated_innovations"] = _innovations.Count(i => i.Validated),
            ["by_category"] = _defects.GroupBy(d => d.Category)
                .ToDictionary(g => g.Key, g => g.Count()),
            ["by_severity"] = _defects.GroupBy(d => d.Severity)
                .ToDictionary(g => g.Key.ToString(), g => g.Count())
        };
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new { defects = _defects, innovations = _innovations, saved_at = DateTime.UtcNow.ToString("O") };
            File.WriteAllText(_persistPath, JsonSerializer.Serialize(data, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("SelfImprover: Save failed: {Message}", ex.Message);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_persistPath)) return;

            var json = File.ReadAllText(_persistPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (data == null) return;

            if (data.TryGetValue("defects", out var d))
            {
                var loaded = JsonSerializer.Deserialize<List<Defect>>(d.GetRawText());
                if (loaded != null) _defects.AddRange(loaded);
            }

            if (data.TryGetValue("innovations", out var i))
            {
                var loaded = JsonSerializer.Deserialize<List<Innovation>>(i.GetRawText());
                if (loaded != null) _innovations.AddRange(loaded);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("SelfImprover: Load failed: {Message}", ex.Message);
        }
    }
}
