using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LTAI.Agent.Tools;

public sealed record TestFailure
{
    public string FilePath { get; init; } = "";
    public int? LineNumber { get; init; }
    public string TestName { get; init; } = "";
    public string Message { get; init; } = "";
    public string? StackTrace { get; init; }
}

public static class TestResultParser
{
    public static List<TestFailure> ParseDotnetTest(string output, string? workspaceRoot = null)
    {
        var failures = new List<TestFailure>();

        var lines = output.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            var errorMatch = Regex.Match(trimmed,
                @"^(.+?)\((\d+),\d+\):\s*(error|warning)\s+(\w+):\s*(.+)$");
            if (errorMatch.Success)
            {
                failures.Add(new TestFailure
                {
                    FilePath = errorMatch.Groups[1].Value,
                    LineNumber = int.Parse(errorMatch.Groups[2].Value),
                    TestName = errorMatch.Groups[4].Value,
                    Message = errorMatch.Groups[5].Value
                });
                continue;
            }

            var failMatch = Regex.Match(trimmed,
                @"^\s*Failed\s+(.+?)\s+\[(\d+)");
            if (failMatch.Success && !failures.Any(f => f.TestName == failMatch.Groups[1].Value))
            {
                failures.Add(new TestFailure
                {
                    TestName = failMatch.Groups[1].Value.Trim(),
                    Message = "Test failed"
                });
            }
        }

        TryParseTrx(output, workspaceRoot, failures);

        return failures;
    }

    public static List<TestFailure> ParseJUnitXml(string xmlContent)
    {
        var failures = new List<TestFailure>();
        try
        {
            var doc = XDocument.Parse(xmlContent);
            var root = doc.Root;
            if (root == null) return failures;

            foreach (var testSuite in root.Elements("testsuite"))
            {
                foreach (var testCase in testSuite.Elements("testcase"))
                {
                    var failure = testCase.Element("failure");
                    if (failure != null)
                    {
                        var name = testCase.Attribute("name")?.Value ?? "unknown";
                        var classAttr = testCase.Attribute("classname")?.Value ?? "";
                        var message = failure.Attribute("message")?.Value ?? failure.Value;
                        failures.Add(new TestFailure
                        {
                            TestName = $"{classAttr}.{name}".TrimStart('.'),
                            Message = message.Length > 500 ? message[..500] : message,
                            StackTrace = failure.Value.Length > 1000 ? failure.Value[..1000] : failure.Value
                        });
                    }
                }
            }
        }
        catch { }
        return failures;
    }

    public static List<TestFailure> ParseJsonResults(string jsonContent)
    {
        var failures = new List<TestFailure>();
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            if (root.TryGetProperty("testResults", out var testResults))
            {
                foreach (var file in testResults.EnumerateArray())
                {
                    var filePath = file.TryGetProperty("name", out var fp) ? fp.GetString() ?? "" : "";
                    if (file.TryGetProperty("assertionResults", out var assertions))
                    {
                        foreach (var a in assertions.EnumerateArray())
                        {
                            var status = a.TryGetProperty("status", out var s) ? s.GetString() : "";
                            if (status == "failed")
                            {
                                failures.Add(new TestFailure
                                {
                                    FilePath = filePath,
                                    LineNumber = a.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? (int?)l.GetInt32() : null,
                                    TestName = a.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                                    Message = a.TryGetProperty("failureMessages", out var fm) && fm.GetArrayLength() > 0
                                        ? (fm[0].GetString() ?? "") : ""
                                });
                            }
                        }
                    }
                }
            }

            if (root.TryGetProperty("tests", out var tests))
            {
                foreach (var test in tests.EnumerateArray())
                {
                    var outcome = test.TryGetProperty("outcome", out var o) ? o.GetString() : "";
                    if (outcome == "failed")
                    {
                        failures.Add(new TestFailure
                        {
                            TestName = test.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                            Message = test.TryGetProperty("message", out var m) ? m.GetString() ?? "" : ""
                        });
                    }
                }
            }
        }
        catch { }
        return failures;
    }

    public static List<TestFailure> Parse(string output)
    {
        var trimmed = output.TrimStart();
        if (trimmed.StartsWith('{'))
            return ParseJsonResults(output);
        if (trimmed.StartsWith('<'))
            return ParseJUnitXml(output);
        return ParseDotnetTest(output);
    }

    public static string BuildFailureContext(List<TestFailure> failures)
    {
        if (failures.Count == 0) return "";
        var lines = new List<string> { $"## {failures.Count} Test Failure(s)" };
        foreach (var f in failures.Take(20))
        {
            var loc = !string.IsNullOrEmpty(f.FilePath) ? $" ({f.FilePath}" + (f.LineNumber.HasValue ? $":{f.LineNumber}" : "") + ")" : "";
            lines.Add($"- **{f.TestName}**{loc}: {f.Message}");
        }
        if (failures.Count > 20)
            lines.Add($"... and {failures.Count - 20} more failures");
        return string.Join("\n", lines);
    }

    private static void TryParseTrx(string output, string? workspaceRoot, List<TestFailure> failures)
    {
        try
        {
            var trxMatch = Regex.Match(output, @"Results File:\s*(.+?\.trx)");
            string? trxPath = null;
            if (trxMatch.Success)
            {
                trxPath = trxMatch.Groups[1].Value.Trim();
                if (workspaceRoot != null && !Path.IsPathRooted(trxPath))
                    trxPath = Path.Combine(workspaceRoot, trxPath);
            }

            if (trxPath != null && File.Exists(trxPath))
            {
                var doc = XDocument.Load(trxPath);
                var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
                foreach (var unitTestResult in doc.Descendants(ns + "UnitTestResult"))
                {
                    var outcome = unitTestResult.Attribute("outcome")?.Value;
                    if (outcome == "Failed")
                    {
                        var outputEl = unitTestResult.Element(ns + "Output");
                        var errorInfo = outputEl?.Element(ns + "ErrorInfo");
                        failures.Add(new TestFailure
                        {
                            TestName = unitTestResult.Attribute("testName")?.Value ?? "unknown",
                            Message = errorInfo?.Element(ns + "Message")?.Value ?? "Test failed",
                            StackTrace = errorInfo?.Element(ns + "StackTrace")?.Value
                        });
                    }
                }
            }
        }
        catch { }
    }
}
