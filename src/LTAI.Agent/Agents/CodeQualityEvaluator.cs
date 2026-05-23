using System.Text.RegularExpressions;

namespace LTAI.Agent.Agents;

public enum QualityDimension
{
    Correctness,
    Maintainability,
    Abstraction,
    Hygiene,
    TestCoverage
}

public sealed class QualityScore
{
    public QualityDimension Dimension { get; init; }
    public double Score { get; init; }
    public string Justification { get; init; } = "";
    public List<string> Issues { get; init; } = new();
}

public sealed class QualityMatrix
{
    public List<QualityScore> Scores { get; init; } = new();
    public double OverallScore => Scores.Count > 0 ? Scores.Average(s => s.Score) : 0;
    public string Grade => OverallScore switch { >= 0.9 => "A", >= 0.75 => "B", >= 0.6 => "C", >= 0.4 => "D", _ => "F" };
}

public static class CodeQualityEvaluator
{
    private static readonly string[] SecurityPatterns =
    {
        @"SqlCommand\(.*""SELECT.*\+|String\.Format\(.*""SELECT",
        @"password\s*=\s*""[^""]{3,}""",
        @"api[_-]?key\s*=\s*""\w{8,}""",
        @"\.Execute\(.*command", @"eval\s*\(.*input",
        @"innerHTML\s*=", @"dangerouslySetInnerHTML"
    };

    private static readonly string[] MaintainabilityPatterns =
    {
        @"\bif\b.*\bif\b.*\bif\b.*\bif\b", // Deep nesting (4+ ifs)
        @"catch\s*\(\s*Exception\s*\)\s*\{\s*\}", // Swallowing exceptions
        @"catch\s*\(\s*\)\s*\{", // Empty catch
        @"public static.*(?!readonly)", // mutable static
    };

    private static readonly string[] AbstractionPatterns =
    {
        @"interface\s+I\w+", @"abstract\s+class",
        @"sealed\s+class|\bwhere\s+T\s*:\s*\w+", // Generics
        @"(?:virtual|override)\s+\w+\s+\w+\(" // Polymorphism
    };

    private static readonly string[] HygienePatterns =
    {
        @"\/\/\s*TODO", @"\/\/\s*FIXME", @"\/\/\s*HACK",
        @"Console\.WriteLine\(", @"Debug\.WriteLine\(", // Debug left in
        @"#region", @"#pragma warning disable"
    };

    private static readonly string[] TestPatterns =
    {
        @"\[Fact\]|\[Test\]|\[TestMethod\]|def test_|it\(.*=>|test\(.*=>",
        @"Assert\.", @"expect\(", @"assert\("
    };

    public static QualityMatrix Evaluate(string code)
    {
        var scores = new List<QualityScore>
        {
            EvaluateCorrectness(code),
            EvaluateMaintainability(code),
            EvaluateAbstraction(code),
            EvaluateHygiene(code),
            EvaluateTestCoverage(code)
        };

        return new QualityMatrix { Scores = scores };
    }

    private static QualityScore EvaluateCorrectness(string code)
    {
        var issues = new List<string>();
        double score = 1.0;

        foreach (var pattern in SecurityPatterns)
        {
            if (Regex.IsMatch(code, pattern, RegexOptions.IgnoreCase))
            {
                issues.Add($"Potential security issue: {pattern[..Math.Min(pattern.Length, 40)]}...");
                score -= 0.2;
            }
        }

        if (Regex.IsMatch(code, @"Thread\.Sleep\("))
        { issues.Add("Thread.Sleep() detected — consider async/await"); score -= 0.05; }

        return new QualityScore
        {
            Dimension = QualityDimension.Correctness,
            Score = Math.Max(score, 0.0),
            Issues = issues,
            Justification = score >= 0.8 ? "No obvious correctness issues" :
                           score >= 0.5 ? "Some potential concerns" : "Critical issues detected"
        };
    }

    private static QualityScore EvaluateMaintainability(string code)
    {
        var issues = new List<string>();
        double score = 1.0;

        foreach (var pattern in MaintainabilityPatterns)
        {
            if (Regex.IsMatch(code, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                issues.Add($"Maintainability issue: {pattern[..Math.Min(pattern.Length, 30)]}");
                score -= 0.15;
            }
        }

        var functionCount = Regex.Matches(code, @"(?:private|public|protected|internal)\s+(?:static\s+)?(?:\w+)\s+(\w+)\s*\(").Count;
        if (functionCount > 0)
        {
            var averageLength = code.Length / Math.Max(functionCount, 1);
            if (averageLength > 2000)
            { issues.Add("Functions may be too long — consider splitting"); score -= 0.1; }
            if (averageLength < 100 && functionCount > 5)
                score += 0.1; // Short functions = good
        }

        return new QualityScore
        {
            Dimension = QualityDimension.Maintainability,
            Score = Math.Clamp(score, 0.0, 1.0),
            Issues = issues,
            Justification = score >= 0.8 ? "Good maintainability" :
                           score >= 0.5 ? "Mixed maintainability" : "Needs refactoring"
        };
    }

    private static QualityScore EvaluateAbstraction(string code)
    {
        var issues = new List<string>();
        double score = 0.3; // Baseline

        foreach (var pattern in AbstractionPatterns)
        {
            var matches = Regex.Matches(code, pattern, RegexOptions.IgnoreCase);
            if (matches.Count > 0)
                score += Math.Min(matches.Count * 0.15, 0.5);
        }

        if (code.Contains("interface ", StringComparison.OrdinalIgnoreCase))
        { score += 0.1; }
        else if (code.Length > 1000)
        { issues.Add("No interfaces defined — consider extracting abstractions"); }

        if (code.Contains("class ") && !code.Contains("sealed") && !code.Contains("abstract"))
            issues.Add("Classes not sealed or abstract — consider explicit inheritance design");

        return new QualityScore
        {
            Dimension = QualityDimension.Abstraction,
            Score = Math.Clamp(score, 0.0, 1.0),
            Issues = issues,
            Justification = score >= 0.7 ? "Good abstraction use" :
                           score >= 0.4 ? "Adequate abstraction" : "Limited abstraction — consider interfaces"
        };
    }

    private static QualityScore EvaluateHygiene(string code)
    {
        var issues = new List<string>();
        double score = 1.0;

        foreach (var pattern in HygienePatterns)
        {
            var matches = Regex.Matches(code, pattern, RegexOptions.IgnoreCase);
            if (matches.Count > 0)
            {
                issues.Add($"Code hygiene: {matches.Count} instance(s) of '{pattern[..Math.Min(pattern.Length, 25)]}'");
                score -= matches.Count * 0.1;
            }
        }

        if (code.Contains("\t"))
        { issues.Add("Tab characters found — use spaces"); score -= 0.05; }

        var lines = code.Split('\n');
        var trailingSpaces = lines.Count(l => l.EndsWith(" ") || l.EndsWith("\t"));
        if (trailingSpaces > 0)
        { issues.Add($"{trailingSpaces} lines have trailing whitespace"); score -= 0.05; }

        return new QualityScore
        {
            Dimension = QualityDimension.Hygiene,
            Score = Math.Max(score, 0.0),
            Issues = issues,
            Justification = score >= 0.9 ? "Clean code" :
                           score >= 0.7 ? "Minor issues" : "Needs cleanup"
        };
    }

    private static QualityScore EvaluateTestCoverage(string code)
    {
        var issues = new List<string>();
        double score = 0.0;

        var testFrameworks = Regex.Matches(code, @"\[Fact\]|\[Test\]|\[TestMethod\]|def test_|it\(", RegexOptions.IgnoreCase);
        var assertions = Regex.Matches(code, @"Assert\.|expect\(|assert\(", RegexOptions.IgnoreCase);

        if (testFrameworks.Count > 0)
        {
            score += Math.Min(testFrameworks.Count * 0.2, 0.6);
            if (assertions.Count > testFrameworks.Count)
                score += 0.2;
            if (Regex.IsMatch(code, @"Mock\b|Moq\b|mock\(|MagicMock"))
                score += 0.1;
        }
        else if (code.Length > 500)
        {
            issues.Add("No test code detected — consider adding unit tests");
            score = 0.1;
        }

        return new QualityScore
        {
            Dimension = QualityDimension.TestCoverage,
            Score = Math.Clamp(score, 0.0, 1.0),
            Issues = issues,
            Justification = score >= 0.7 ? "Good test coverage" :
                           score >= 0.3 ? "Some tests present" : "Missing or minimal tests"
        };
    }

    public static string FormatReport(QualityMatrix matrix)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Code Quality Report — Overall: {matrix.OverallScore:F2} ({matrix.Grade})");
        sb.AppendLine();

        foreach (var s in matrix.Scores.OrderByDescending(s => s.Score))
        {
            var bar = new string(s.Score >= 0.8 ? '█' : s.Score >= 0.5 ? '▓' : '░', (int)(s.Score * 20));
            sb.AppendLine($"### {s.Dimension}: {s.Score:F2} {bar}");
            sb.AppendLine($"  {s.Justification}");
            foreach (var issue in s.Issues)
                sb.AppendLine($"  - ⚠️ {issue}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
