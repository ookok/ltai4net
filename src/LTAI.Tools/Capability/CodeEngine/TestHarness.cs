using System.Diagnostics;
using System.Text.RegularExpressions;
using LTAI.Tools.CodeGraph;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tools.CodeEngine;

public sealed class TestResult
{
    public bool Success { get; init; }
    public string Framework { get; init; } = "";
    public string Command { get; init; } = "";
    public int Total { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public double DurationMs { get; init; }
    public List<TestCase> Cases { get; init; } = new();
    public string RawOutput { get; init; } = "";
    public double PassRate => Total > 0 ? (double)Passed / Total : 0;
}

public sealed class TestCase
{
    public string Name { get; init; } = "";
    public string Status { get; init; } = "";
    public double DurationMs { get; init; }
    public string? Error { get; init; }
    public string? StackTrace { get; init; }
    public string? File { get; init; }
    public int Line { get; init; }
}

public sealed class TestHarness
{
    private readonly CodeGraphEnhanced? _codeGraph;
    private readonly ILogger<TestHarness> _logger;

    private static readonly Regex s_dotNetPassedPattern = new(
        @"Passed\s+(?<name>[^(]+)\s*\((?<duration>[\d.]+)\s*\w*\)",
        RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex s_dotNetFailedPattern = new(
        @"Failed\s+(?<name>[^(]+)\s*\((?<duration>[\d.]+)\s*\w*\)",
        RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex s_dotNetSkippedPattern = new(
        @"Skipped\s+(?<name>[^(]+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex s_pytestPattern = new(
        @"^(?<status>PASSED|FAILED|SKIPPED|XFAIL|XPASS)\s+(?<name>\S+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex s_jestPassPattern = new(
        @"✓\s+(?<name>.+?)(?:\s+\((?<duration>\d+)\s*ms\))?$",
        RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex s_jestFailPattern = new(
        @"✕\s+(?<name>.+?)(?:\s+\((?<duration>\d+)\s*ms\))?$",
        RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex s_jestSkipPattern = new(
        @"○\s+skipped\s+(?<name>.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex s_cargoTestPattern = new(
        @"test\s+(?<name>\S+)\s+\.\.\.\s+(?<status>ok|FAILED|ignored)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex s_goTestPassPattern = new(
        @"^--- PASS:\s+(?<name>\S+)",
        RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex s_goTestFailPattern = new(
        @"^--- FAIL:\s+(?<name>\S+)",
        RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex s_goTestSkipPattern = new(
        @"^--- SKIP:\s+(?<name>\S+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex s_genericPattern = new(
        @"(?<status>PASS|FAIL|SKIP|OK|ERROR)[:\s]+(?<name>.+)$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public TestHarness(CodeGraphEnhanced? codeGraph = null, ILogger<TestHarness>? logger = null)
    {
        _codeGraph = codeGraph;
        _logger = logger ?? NullLogger<TestHarness>.Instance;
    }

    public async Task<TestResult> RunTestsAsync(
        string? rootPath = null,
        string? filter = null,
        string? configuration = null,
        int timeoutMs = 300000)
    {
        rootPath ??= Directory.GetCurrentDirectory();
        configuration ??= "Debug";

        var framework = DetectTestFramework(rootPath);
        var (command, args) = GetTestCommand(framework, rootPath, filter, configuration);

        var sw = Stopwatch.StartNew();
        var (exitCode, output) = await RunProcessAsync(command, args, rootPath, timeoutMs).ConfigureAwait(false);
        sw.Stop();

        var cases = ParseTestOutput(output, framework);

        return new TestResult
        {
            Success = exitCode == 0,
            Framework = framework,
            Command = $"{command} {args}",
            Total = cases.Count,
            Passed = cases.Count(c => c.Status == "passed"),
            Failed = cases.Count(c => c.Status == "failed"),
            Skipped = cases.Count(c => c.Status == "skipped"),
            DurationMs = sw.ElapsedMilliseconds,
            Cases = cases,
            RawOutput = output.Length > 10000 ? output[..10000] + "\n... (truncated)" : output,
        };
    }

    public async Task<TestResult> RunAffectedTestsAsync(
        string rootPath,
        List<string> changedSymbols,
        string? configuration = null,
        int timeoutMs = 300000)
    {
        if (_codeGraph == null)
        {
            _logger.LogWarning("CodeGraphEnhanced not available, running all tests");
            return await RunTestsAsync(rootPath, configuration: configuration, timeoutMs: timeoutMs).ConfigureAwait(false);
        }

        var affectedFiles = new HashSet<string>();
        foreach (var symbol in changedSymbols)
        {
            var impact = _codeGraph.GetImpactRadius(symbol, 3);
            foreach (var node in impact.AffectedNodes)
            {
                if (node.File.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
                    node.File.Contains("Tests", StringComparison.OrdinalIgnoreCase))
                {
                    affectedFiles.Add(node.File);
                }
            }
        }

        if (affectedFiles.Count == 0)
        {
            _logger.LogInformation("No affected test files found for {Count} changed symbols", changedSymbols.Count);
            return new TestResult { Success = true, Framework = "none", Total = 0 };
        }

        var filter = string.Join("|", affectedFiles
            .Select(Path.GetFileNameWithoutExtension)
            .Distinct()
            .Take(10));

        _logger.LogInformation("Running affected tests: {Filter} ({Count} files)", filter, affectedFiles.Count);
        return await RunTestsAsync(rootPath, filter, configuration, timeoutMs).ConfigureAwait(false);
    }

    public async Task<List<string>> GetTestsCoveringSymbolAsync(string rootPath, string symbolName)
    {
        if (_codeGraph == null) return new();

        var impact = _codeGraph.GetImpactRadius(symbolName, 3);
        return impact.AffectedNodes
            .Where(n => n.File.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
                        n.File.Contains("Tests", StringComparison.OrdinalIgnoreCase))
            .Select(n => $"{n.Name} ({n.File}:{n.Line})")
            .Distinct()
            .ToList();
    }

    public static string DetectTestFramework(string rootPath)
    {
        var allFiles = Directory.GetFiles(rootPath, "*.*", SearchOption.AllDirectories)
            .Select(f => Path.GetFileName(f).ToLowerInvariant())
            .ToList();

        var allContent = "";
        foreach (var projFile in Directory.GetFiles(rootPath, "*.csproj", SearchOption.AllDirectories).Take(5))
        {
            try { allContent += File.ReadAllText(projFile); } catch { }
        }

        if (allContent.Contains("xunit", StringComparison.OrdinalIgnoreCase)) return "xunit";
        if (allContent.Contains("nunit", StringComparison.OrdinalIgnoreCase)) return "nunit";
        if (allContent.Contains("MSTest", StringComparison.OrdinalIgnoreCase) ||
            allContent.Contains("Microsoft.VisualStudio.TestPlatform", StringComparison.OrdinalIgnoreCase))
            return "mstest";

        if (allFiles.Contains("jest.config.js") || allFiles.Contains("jest.config.ts") ||
            allContent.Contains("\"jest\"", StringComparison.OrdinalIgnoreCase))
            return "jest";

        if (allFiles.Contains("vitest.config.ts") || allFiles.Contains("vitest.config.js"))
            return "vitest";

        if (allContent.Contains("\"mocha\"", StringComparison.OrdinalIgnoreCase))
            return "mocha";

        if (allFiles.Contains("pytest.ini") || allFiles.Contains("conftest.py") ||
            allContent.Contains("pytest", StringComparison.OrdinalIgnoreCase))
            return "pytest";

        if (allFiles.Contains("unittest") || allContent.Contains("unittest", StringComparison.OrdinalIgnoreCase))
            return "unittest";

        if (allContent.Contains("Cargo.toml") || allFiles.Contains("cargo.toml"))
        {
            try
            {
                var cargoContent = File.ReadAllText(Path.Combine(rootPath, "Cargo.toml"));
                if (cargoContent.Contains("[dev-dependencies]"))
                    return "cargo-test";
            }
            catch { }
        }

        if (allFiles.Any(f => f.EndsWith("_test.go") || f.EndsWith("_tests.go")))
            return "go-test";

        return "dotnet-test";
    }

    private static (string command, string args) GetTestCommand(
        string framework, string rootPath, string? filter, string? configuration)
    {
        return framework switch
        {
            "xunit" or "nunit" or "mstest" or "dotnet-test" =>
                ("dotnet", $"test -c {configuration ?? "Debug"} --nologo" +
                    (filter != null ? $" --filter \"FullyQualifiedName~{filter}\"" : "")),

            "pytest" =>
                ("pytest", (filter != null ? $"-k \"{filter}\"" : "") + " -v --tb=short"),

            "jest" => ("npx", $"jest --no-coverage --verbose" +
                (filter != null ? $" --testNamePattern=\"{filter}\"" : "")),

            "vitest" => ("npx", $"vitest run --reporter verbose" +
                (filter != null ? $" -t \"{filter}\"" : "")),

            "mocha" => ("npx", $"mocha --reporter spec" +
                (filter != null ? $" --grep \"{filter}\"" : "")),

            "cargo-test" =>
                ("cargo", $"test" + (filter != null ? $" {filter}" : "")),

            "go-test" =>
                ("go", $"test ./..." + (filter != null ? $" -run \"{filter}\"" : "")),

            _ => ("dotnet", $"test -c {configuration ?? "Debug"} --nologo" +
                (filter != null ? $" --filter \"{filter}\"" : "")),
        };
    }

    private static List<TestCase> ParseTestOutput(string output, string framework)
    {
        return framework switch
        {
            "xunit" or "nunit" or "mstest" or "dotnet-test" => ParseDotNetTestOutput(output),
            "pytest" => ParsePytestOutput(output),
            "jest" or "vitest" or "mocha" => ParseJestOutput(output),
            "cargo-test" => ParseCargoTestOutput(output),
            "go-test" => ParseGoTestOutput(output),
            _ => ParseGenericTestOutput(output),
        };
    }

    private static List<TestCase> ParseDotNetTestOutput(string output)
    {
        var cases = new List<TestCase>();

        foreach (Match m in s_dotNetPassedPattern.Matches(output))
        {
            cases.Add(new TestCase
            {
                Name = m.Groups["name"].Value.Trim(),
                Status = "passed",
                DurationMs = double.TryParse(m.Groups["duration"].Value, out var d) ? d : 0,
            });
        }

        foreach (Match m in s_dotNetFailedPattern.Matches(output))
        {
            cases.Add(new TestCase
            {
                Name = m.Groups["name"].Value.Trim(),
                Status = "failed",
                DurationMs = double.TryParse(m.Groups["duration"].Value, out var d) ? d : 0,
                Error = ExtractErrorForTest(output, m.Groups["name"].Value.Trim()),
            });
        }

        foreach (Match m in s_dotNetSkippedPattern.Matches(output))
        {
            cases.Add(new TestCase
            {
                Name = m.Groups["name"].Value.Trim(),
                Status = "skipped",
            });
        }

        return cases;
    }

    private static List<TestCase> ParsePytestOutput(string output)
    {
        var cases = new List<TestCase>();

        foreach (Match m in s_pytestPattern.Matches(output))
        {
            var status = m.Groups["status"].Value;
            cases.Add(new TestCase
            {
                Name = m.Groups["name"].Value,
                Status = status switch
                {
                    "PASSED" or "XPASS" => "passed",
                    "FAILED" => "failed",
                    "SKIPPED" or "XFAIL" => "skipped",
                    _ => "unknown",
                },
                Error = status == "FAILED" ? ExtractPytestError(output, m.Groups["name"].Value) : null,
            });
        }

        return cases;
    }

    private static List<TestCase> ParseJestOutput(string output)
    {
        var cases = new List<TestCase>();

        foreach (Match m in s_jestPassPattern.Matches(output))
        {
            cases.Add(new TestCase
            {
                Name = m.Groups["name"].Value.Trim(),
                Status = "passed",
                DurationMs = double.TryParse(m.Groups["duration"].Value, out var d) ? d : 0,
            });
        }

        foreach (Match m in s_jestFailPattern.Matches(output))
        {
            cases.Add(new TestCase
            {
                Name = m.Groups["name"].Value.Trim(),
                Status = "failed",
                DurationMs = double.TryParse(m.Groups["duration"].Value, out var d) ? d : 0,
                Error = ExtractErrorForTest(output, m.Groups["name"].Value.Trim()),
            });
        }

        foreach (Match m in s_jestSkipPattern.Matches(output))
        {
            cases.Add(new TestCase
            {
                Name = m.Groups["name"].Value.Trim(),
                Status = "skipped",
            });
        }

        return cases;
    }

    private static List<TestCase> ParseCargoTestOutput(string output)
    {
        var cases = new List<TestCase>();

        foreach (Match m in s_cargoTestPattern.Matches(output))
        {
            var status = m.Groups["status"].Value;
            cases.Add(new TestCase
            {
                Name = m.Groups["name"].Value,
                Status = status switch
                {
                    "ok" => "passed",
                    "FAILED" => "failed",
                    "ignored" => "skipped",
                    _ => "unknown",
                },
                Error = status == "FAILED" ? ExtractErrorForTest(output, m.Groups["name"].Value) : null,
            });
        }

        return cases;
    }

    private static List<TestCase> ParseGoTestOutput(string output)
    {
        var cases = new List<TestCase>();

        foreach (Match m in s_goTestPassPattern.Matches(output))
        {
            cases.Add(new TestCase { Name = m.Groups["name"].Value, Status = "passed" });
        }

        foreach (Match m in s_goTestFailPattern.Matches(output))
        {
            cases.Add(new TestCase
            {
                Name = m.Groups["name"].Value,
                Status = "failed",
                Error = ExtractErrorForTest(output, m.Groups["name"].Value),
            });
        }

        foreach (Match m in s_goTestSkipPattern.Matches(output))
        {
            cases.Add(new TestCase { Name = m.Groups["name"].Value, Status = "skipped" });
        }

        return cases;
    }

    private static List<TestCase> ParseGenericTestOutput(string output)
    {
        var cases = new List<TestCase>();

        foreach (Match m in s_genericPattern.Matches(output))
        {
            var status = m.Groups["status"].Value.ToUpperInvariant();
            cases.Add(new TestCase
            {
                Name = m.Groups["name"].Value.Trim(),
                Status = status switch
                {
                    "PASS" or "OK" => "passed",
                    "FAIL" or "ERROR" => "failed",
                    "SKIP" => "skipped",
                    _ => "unknown",
                },
            });
        }

        return cases;
    }

    private static string? ExtractErrorForTest(string output, string testName)
    {
        var idx = output.IndexOf(testName, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var start = Math.Max(0, idx);
        var end = Math.Min(output.Length, start + 2000);
        var section = output[start..end];

        var errorMatch = Regex.Match(section,
            @"(?:Error|Exception|Assert|assert|panic|FAILED)[:\s]+(.+?)(?:\n|$)",
            RegexOptions.IgnoreCase);

        return errorMatch.Success
            ? errorMatch.Value.Trim()[..Math.Min(500, errorMatch.Value.Length)]
            : section[..Math.Min(500, section.Length)];
    }

    private static string? ExtractPytestError(string output, string testName)
    {
        var idx = output.IndexOf(testName, StringComparison.Ordinal);
        if (idx < 0) return null;

        var start = idx + testName.Length;
        var end = Math.Min(output.Length, start + 2000);

        var errorMatch = Regex.Match(output[start..end],
            @"(?:E\s+|Error|AssertionError|assert)\s*(.+?)(?:\n(?!E\s+))",
            RegexOptions.Singleline);

        return errorMatch.Success
            ? errorMatch.Value.Trim()[..Math.Min(500, errorMatch.Value.Length)]
            : output.Substring(start, Math.Min(500, end - start)).Trim();
    }

    private static async Task<(int exitCode, string output)> RunProcessAsync(
        string command, string args, string workingDir, int timeoutMs)
    {
        var psi = new ProcessStartInfo(command, args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi };

        var output = new System.Text.StringBuilder();
        var error = new System.Text.StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) output.AppendLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) error.AppendLine(e.Data);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var completed = await Task.Run(() => proc.WaitForExit(timeoutMs)).ConfigureAwait(false);
        if (!completed)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (-1, output + "\n[TEST TIMED OUT]\n" + error);
        }

        return (proc.ExitCode, output + "\n" + error);
    }
}
