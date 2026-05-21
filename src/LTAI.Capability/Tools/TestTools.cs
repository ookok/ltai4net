using System.ComponentModel;
using System.Text.Json;
using LTAI.Capability.CodeEngine;
using Microsoft.Extensions.Logging;

namespace LTAI.Capability.Tools;

[Description("Test operations: run tests, affected tests, test framework detection")]
public sealed class TestTools
{
    private readonly TestHarness _harness;
    private readonly ILogger<TestTools> _logger;

    public TestTools(TestHarness harness, ILogger<TestTools>? logger = null)
    {
        _harness = harness;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TestTools>.Instance;
    }

    [Description("Run project tests. Auto-detects test framework (xunit/nunit/mstest/pytest/jest/go-test/cargo-test). Returns structured results.")]
    public async Task<string> TestRun(
        [Description("Root path of the project")] string? path = null,
        [Description("Filter: test name or pattern to run (e.g. 'LoginTests')")] string? filter = null,
        [Description("Build configuration")] string? configuration = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _harness.RunTestsAsync(path, filter, configuration);

        return JsonSerializer.Serialize(new
        {
            result.Success,
            framework = result.Framework,
            command = result.Command,
            total = result.Total,
            passed = result.Passed,
            failed = result.Failed,
            skipped = result.Skipped,
            passRate = Math.Round(result.PassRate * 100, 1),
            durationMs = result.DurationMs,
            failures = result.Cases
                .Where(c => c.Status == "failed")
                .Take(10)
                .Select(c => new { c.Name, c.DurationMs, c.Error }),
            skippedTests = result.Cases
                .Where(c => c.Status == "skipped")
                .Take(5)
                .Select(c => new { c.Name }),
        });
    }

    [Description("Run only tests affected by changed symbols. Uses code graph blast radius to find relevant tests.")]
    public async Task<string> TestRunAffected(
        [Description("Root path of the project")] string path,
        [Description("JSON array of changed symbol names")] string changedSymbolsJson,
        [Description("Build configuration")] string? configuration = null,
        CancellationToken cancellationToken = default)
    {
        var changedSymbols = new List<string>();
        try
        {
            var arr = JsonSerializer.Deserialize<JsonElement>(changedSymbolsJson);
            if (arr.ValueKind == JsonValueKind.Array)
                changedSymbols = arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
        }
        catch
        {
            changedSymbols = changedSymbolsJson.Split(',', ';', '\n')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }

        var result = await _harness.RunAffectedTestsAsync(path, changedSymbols, configuration);

        return JsonSerializer.Serialize(new
        {
            result.Success,
            framework = result.Framework,
            total = result.Total,
            passed = result.Passed,
            failed = result.Failed,
            skipped = result.Skipped,
            passRate = Math.Round(result.PassRate * 100, 1),
            durationMs = result.DurationMs,
            changedSymbols = changedSymbols,
            failures = result.Cases.Where(c => c.Status == "failed").Take(10)
                .Select(c => new { c.Name, c.Error }),
        });
    }

    [Description("Find tests that cover a specific symbol. Uses code graph to trace callers in test files.")]
    public async Task<string> TestFindCovering(
        [Description("Root path of the project")] string path,
        [Description("Symbol name to find tests for (function/class name)")] string symbolName,
        CancellationToken cancellationToken = default)
    {
        var tests = await _harness.GetTestsCoveringSymbolAsync(path, symbolName);
        return JsonSerializer.Serialize(new
        {
            symbolName,
            testCount = tests.Count,
            tests,
        });
    }

    [Description("Detect the test framework used by the project (xunit, nunit, mstest, pytest, jest, etc.).")]
    public string TestDetectFramework(
        [Description("Root path of the project")] string? path = null)
    {
        path ??= Directory.GetCurrentDirectory();
        var framework = TestHarness.DetectTestFramework(path);
        return JsonSerializer.Serialize(new { framework, path });
    }

    [Description("Run build and then tests in one step. Fails fast if build fails.")]
    public async Task<string> TestBuildAndRun(
        [Description("Root path of the project")] string? path = null,
        [Description("Build configuration")] string? configuration = null,
        [Description("Test filter")] string? filter = null,
        CancellationToken cancellationToken = default)
    {
        path ??= Directory.GetCurrentDirectory();

        var buildPipeline = new BuildPipeline();
        var buildResult = await buildPipeline.BuildAsync(path, configuration);

        if (!buildResult.Success)
        {
            return JsonSerializer.Serialize(new
            {
                stage = "build",
                buildResult.Success,
                buildErrors = buildResult.Errors.Take(10).Select(e => new
                { e.File, e.Line, e.Code, e.Message }),
                message = "Build failed, tests not run",
            });
        }

        var testResult = await _harness.RunTestsAsync(path, filter, configuration);

        return JsonSerializer.Serialize(new
        {
            stage = "build+test",
            build = new { buildResult.Success, buildResult.DurationMs },
            test = new
            {
                testResult.Success,
                testResult.Framework,
                testResult.Total,
                testResult.Passed,
                testResult.Failed,
                testResult.Skipped,
                testResult.DurationMs,
            },
        });
    }
}
