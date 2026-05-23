using LTAI.Agent.Agents;
using Xunit;

namespace LTAI.Tests;

public class SWEAtlasTests
{
    [Fact]
    public void QualityEvaluator_ScoresCleanCode_Highly()
    {
        var code = """
            public sealed class Calculator : ICalculator
            {
                public double Add(double a, double b) => a + b;
                public double Subtract(double a, double b) => a - b;
            }

            public interface ICalculator
            {
                double Add(double a, double b);
                double Subtract(double a, double b);
            }
            """;

        var matrix = CodeQualityEvaluator.Evaluate(code);
        Assert.True(matrix.OverallScore >= 0.5);
        Assert.Equal(5, matrix.Scores.Count);
    }

    [Fact]
    public void QualityEvaluator_DetectsSecurityIssues()
    {
        var code = "var cmd = new SqlCommand(\"SELECT * FROM users WHERE name = '\" + input + \"'\");";
        var matrix = CodeQualityEvaluator.Evaluate(code);
        Assert.NotEmpty(matrix.Scores);
    }

    [Fact]
    public void QualityEvaluator_DetectsDeepNesting()
    {
        var code = "if (a) { if (b) { if (c) { if (d) { return; } } } }";
        var matrix = CodeQualityEvaluator.Evaluate(code);
        Assert.NotEmpty(matrix.Scores);
    }

    [Fact]
    public void QualityEvaluator_DetectsTestCode()
    {
        var code = "[Fact] public void TestAdd() { Assert.Equal(3, Add(1, 2)); }";
        var matrix = CodeQualityEvaluator.Evaluate(code);
        Assert.NotNull(matrix);
    }

    [Fact]
    public void QualityEvaluator_DetectsMissingTests_InLargeCode()
    {
        var code = new string('x', 600);
        var matrix = CodeQualityEvaluator.Evaluate(code);
        Assert.Contains(matrix.Scores[4].Issues, i => i.Contains("No test code"));
    }

    [Fact]
    public void QualityEvaluator_FormatReport_IsNotEmpty()
    {
        var matrix = CodeQualityEvaluator.Evaluate("public void Foo() { }");
        var report = CodeQualityEvaluator.FormatReport(matrix);
        Assert.Contains("Code Quality Report", report);
        Assert.Contains("Overall", report);
    }

    [Fact]
    public void RefactoringEvaluator_ExtractedInterface_ScoresHigher()
    {
        var before = """
            public class Calculator {
                public double Add(double a, double b) { return a + b; }
                public double Subtract(double a, double b) { return a - b; }
            }
            """;

        var after = """
            public interface ICalculator {
                double Add(double a, double b);
                double Subtract(double a, double b);
            }
            public sealed class Calculator : ICalculator {
                public double Add(double a, double b) => a + b;
                public double Subtract(double a, double b) => a - b;
            }

            [Fact]
            public void TestAdd() { Assert.Equal(3, new Calculator().Add(1, 2)); }
            """;

        var eval = RefactoringQualityEvaluator.Evaluate(before, after);
        Assert.True(eval.OverallScore >= 0.6);
        Assert.True(eval.Metrics.InterfacesExtracted >= 1);
        Assert.True(eval.Metrics.NewTests >= 1);
    }

    [Fact]
    public void RefactoringEvaluator_DetectsBreakingChange()
    {
        var before = "public void Foo() { } public void Bar() { } public void Baz() { } public void Qux() { }";
        var after = "public void Foo() { }";

        var eval = RefactoringQualityEvaluator.Evaluate(before, after);
        Assert.NotNull(eval);
    }

    [Fact]
    public void RefactoringEvaluator_DetectsNotImplemented()
    {
        var after = "public void Foo() { throw new NotImplementedException(); }";
        var eval = RefactoringQualityEvaluator.Evaluate("", after);
        Assert.Contains(eval.Metrics.RiskItems, r => r.Contains("NotImplemented"));
    }

    [Fact]
    public void RefactoringEvaluator_FormatReport_IsNotEmpty()
    {
        var eval = RefactoringQualityEvaluator.Evaluate("var x = 1;", "var x = 2;");
        var report = RefactoringQualityEvaluator.FormatReport(eval);
        Assert.Contains("Refactoring Quality Report", report);
    }

    [Fact]
    public void ExplorationStrategy_BuildsMultiRoundPrompt()
    {
        var strategy = new CodeExplorationStrategy(3);
        var prompt = strategy.BuildExplorationPrompt("refactor authentication module");

        Assert.Contains("Round 1", prompt);
        Assert.Contains("Round 2", prompt);
        Assert.Contains("Round 3", prompt);
    }

    [Fact]
    public void ExplorationStrategy_ShouldContinue_StopsAtMaxRounds()
    {
        var strategy = new CodeExplorationStrategy(2);
        Assert.False(strategy.ShouldContinueExploration(2, 0.5, 1));
        Assert.True(strategy.ShouldContinueExploration(1, 0.5, 3));
    }

    [Fact]
    public void ExplorationStrategy_ShouldContinue_StopsAtHighConfidence()
    {
        var strategy = new CodeExplorationStrategy(3);
        Assert.False(strategy.ShouldContinueExploration(1, 0.95, 5));
    }

    [Fact]
    public void ExplorationStrategy_ShouldContinue_StopsWhenNoNewFiles()
    {
        var strategy = new CodeExplorationStrategy(3);
        Assert.False(strategy.ShouldContinueExploration(2, 0.5, 0));
    }

    [Fact]
    public void ExplorationStrategy_NextRoundPrompt_IncludesExaminedFiles()
    {
        var strategy = new CodeExplorationStrategy(3);
        var examined = new HashSet<string> { "Auth.cs", "TokenService.cs" };
        var prompt = strategy.BuildNextRoundPrompt("found auth issues", 2, examined);

        Assert.Contains("Auth.cs", prompt);
        Assert.Contains("TokenService.cs", prompt);
        Assert.Contains("Round 2/3", prompt);
    }
}
