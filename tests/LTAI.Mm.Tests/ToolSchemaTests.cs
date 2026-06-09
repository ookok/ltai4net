using LTAI.Mm.Ir;
using LTAI.Mm.ToolSchema;
using Xunit;

namespace LTAI.Mm.Tests;

public class ToolSchemaTests
{
    public class TestTool
    {
        public string TestMethod(
            [MM("desc=用户名; min=1; max=50; pattern=^[a-z]+$")]
            string name,
            [MM("desc=年龄; min=0; max=150")]
            int age,
            string normal)
        {
            return $"{name}:{age}";
        }
    }

    [Fact]
    public void Validate_Passes_For_Valid_Input()
    {
        var method = typeof(TestTool).GetMethod(nameof(TestTool.TestMethod))!;
        var parameters = method.GetParameters();

        var error = MmToolValidator.ValidateInput("Test", parameters, ["alice", 25, "x"]);
        Assert.Null(error);
    }

    [Fact]
    public void Validate_Fails_For_Invalid_Min()
    {
        var method = typeof(TestTool).GetMethod(nameof(TestTool.TestMethod))!;
        var parameters = method.GetParameters();

        var error = MmToolValidator.ValidateInput("Test", parameters, ["", 25, "x"]);
        Assert.NotNull(error);
        Assert.Contains("minimum", error, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_Fails_For_Invalid_Max()
    {
        var method = typeof(TestTool).GetMethod(nameof(TestTool.TestMethod))!;
        var parameters = method.GetParameters();

        var error = MmToolValidator.ValidateInput("Test", parameters, ["alice", 200, "x"]);
        Assert.NotNull(error);
        Assert.Contains("maximum", error, System.StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public void Parameter_Without_MM_Passes_Through()
    {
        var method = typeof(TestTool).GetMethod(nameof(TestTool.TestMethod))!;
        var parameters = method.GetParameters();

        var error = MmToolValidator.ValidateInput("Test", parameters, ["alice", 25, "anything"]);
        Assert.Null(error);
    }

    [Fact]
    public void Constraints_Extracted_Correctly()
    {
        var method = typeof(TestTool).GetMethod(nameof(TestTool.TestMethod))!;
        var nameParam = method.GetParameters()[0];

        var constraints = MmToolSchemaBuilder.GetMmConstraints(nameParam);
        Assert.Equal("1", constraints["minimum"]);
        Assert.Equal("50", constraints["maximum"]);
        Assert.Equal("^[a-z]+$", constraints["pattern"]);
    }
}
