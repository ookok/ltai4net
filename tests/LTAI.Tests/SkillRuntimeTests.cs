using LTAI.Agent.Skills.Runtime;
using LTAI.Models;
using Xunit;

namespace LTAI.Tests;

public sealed class SkillRuntimeTests
{
    [Fact]
    public void Runtime_01_SkillValue_StringOps()
    {
        var a = SkillValue.FromString("hello");
        var b = SkillValue.FromString(" world");

        var result = a + b;
        Assert.Equal("hello world", result.Text);
    }

    [Fact]
    public void Runtime_02_SkillValue_NumberOps()
    {
        var a = SkillValue.FromNumber(10);
        var b = SkillValue.FromNumber(3);

        Assert.Equal(13, (a + b).Number, 0.01);
        Assert.Equal(7, (a - b).Number, 0.01);
        Assert.Equal(30, (a * b).Number, 0.01);
        Assert.Equal(3.33, (a / b).Number, 0.01);
    }

    [Fact]
    public void Runtime_03_SkillValue_Comparison()
    {
        var a = SkillValue.FromNumber(5);
        var b = SkillValue.FromNumber(3);

        Assert.True((a > b).Bool);
        Assert.False((a < b).Bool);
        Assert.True((SkillValue.FromString("abc") == SkillValue.FromString("abc")).Bool);
    }

    [Fact]
    public void Runtime_04_VarScope_SetAndGet()
    {
        var scope = new SkillVarScope();

        scope.Set("x", SkillValue.FromNumber(42));
        scope.Set("name", SkillValue.FromString("test"));

        Assert.Equal(42, scope.Get("x").Number, 0.01);
        Assert.Equal("test", scope.Get("name").Text);
    }

    [Fact]
    public void Runtime_05_VarScope_Builtins()
    {
        var scope = new SkillVarScope();

        Assert.True(scope.Get("true").Bool);
        Assert.False(scope.Get("false").Bool);
        Assert.NotEmpty(scope.Get("date").Text);
    }

    [Fact]
    public void Runtime_06_VarScope_Resolve()
    {
        var scope = new SkillVarScope();

        Assert.Equal("hello", scope.Resolve("\"hello\"").Text);
        Assert.Equal(3.14, scope.Resolve("3.14").Number, 0.01);
        Assert.True(scope.Resolve("true").Bool);
    }

    [Fact]
    public void Runtime_07_Expression_Arithmetic()
    {
        var scope = new SkillVarScope();
        var expr = new SkillExpressionEngine(scope);

        scope.Set("x", SkillValue.FromNumber(10));
        scope.Set("y", SkillValue.FromNumber(5));

        Assert.Equal(15, expr.Evaluate("$x + $y").Number, 0.01);
        Assert.Equal(5, expr.Evaluate("$x - $y").Number, 0.01);
    }

    [Fact]
    public void Runtime_08_Expression_Comparison()
    {
        var scope = new SkillVarScope();
        var expr = new SkillExpressionEngine(scope);

        scope.Set("a", SkillValue.FromNumber(3));

        Assert.True(expr.Evaluate("$a > 0").Bool);
        Assert.False(expr.Evaluate("$a < 0").Bool);
    }

    [Fact]
    public void Runtime_09_Expression_Ternary()
    {
        var scope = new SkillVarScope();
        var expr = new SkillExpressionEngine(scope);

        scope.Set("x", SkillValue.FromNumber(10));

        var result = expr.Evaluate("$x > 5 ? \"big\" : \"small\"");
        Assert.Equal("big", result.Text);

        scope.Set("x", SkillValue.FromNumber(2));
        result = expr.Evaluate("$x > 5 ? \"big\" : \"small\"");
        Assert.Equal("small", result.Text);
    }

    [Fact]
    public void Runtime_10_Expression_Interpolation()
    {
        var scope = new SkillVarScope();
        var expr = new SkillExpressionEngine(scope);

        scope.Set("name", SkillValue.FromString("LTAI"));
        scope.Set("count", SkillValue.FromNumber(5));

        var result = expr.Interpolate("Found {{ $count }} items for {{ $name }}");
        Assert.Equal("Found 5 items for LTAI", result);
    }

    [Fact]
    public void Runtime_11_Branch_ParseAndSelect()
    {
        var scope = new SkillVarScope();
        var expr = new SkillExpressionEngine(scope);
        var branches = new SkillBranchEngine(expr);

        scope.Set("count", SkillValue.FromNumber(3));

        var lines = new List<string>
        {
            "## 分支 when $count > 0",
            "1. 有结果",
            "## 分支 when $count == 0",
            "1. 无结果"
        };

        var parsed = SkillBranchEngine.ParseBranches(lines);
        Assert.Equal(2, parsed.Count);

        var selected = branches.SelectBranch(parsed);
        Assert.NotNull(selected);
        Assert.Equal("$count > 0", selected!.Condition);
    }

    [Fact]
    public void Runtime_12_StepExecutor_CaptureVariable()
    {
        var action = "shell: dotnet build → $build_output";
        var varName = SkillStepExecutor.ExtractCaptureVariable(action);

        Assert.Equal("build_output", varName);
    }
}
