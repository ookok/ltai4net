using LTAI.Agent.Workflows.GraphIR;
using Xunit;

namespace LTAI.Tests;

/// <summary>
/// Tests for <see cref="WorkflowCompiler"/> fallback (no-LLM) path.
/// All tests create a compiler with llm=null so fallback logic is exercised.
/// </summary>
public sealed class WorkflowCompilerTests
{
    private static WorkflowCompiler Create() => new(llm: null);

    [Fact]
    public async Task AssignRolesAsync_CodingIntent_ReturnsCoder()
    {
        var compiler = Create();
        var roles = await compiler.AssignRolesAsync("编写一个 REST API 服务", default);
        Assert.Contains(roles, r => r.Name == "coder");
    }

    [Fact]
    public async Task AssignRolesAsync_EnglishCodeIntent_ReturnsCoder()
    {
        var compiler = Create();
        var roles = await compiler.AssignRolesAsync("implement a login feature", default);
        Assert.Contains(roles, r => r.Name == "coder");
    }

    [Fact]
    public async Task AssignRolesAsync_ReviewIntent_ReturnsReviewer()
    {
        var compiler = Create();
        var roles = await compiler.AssignRolesAsync("审查代码质量", default);
        Assert.Contains(roles, r => r.Name == "reviewer");
    }

    [Fact]
    public async Task AssignRolesAsync_TestIntent_ReturnsTester()
    {
        var compiler = Create();
        var roles = await compiler.AssignRolesAsync("写单元测试", default);
        Assert.Contains(roles, r => r.Name == "tester");
    }

    [Fact]
    public async Task AssignRolesAsync_DeployIntent_ReturnsDevops()
    {
        var compiler = Create();
        var roles = await compiler.AssignRolesAsync("部署到生产环境", default);
        Assert.Contains(roles, r => r.Name == "devops");
    }

    [Fact]
    public async Task AssignRolesAsync_DesignIntent_ReturnsPlanner()
    {
        var compiler = Create();
        var roles = await compiler.AssignRolesAsync("设计数据库 schema", default);
        Assert.Contains(roles, r => r.Name == "planner");
    }

    [Fact]
    public async Task AssignRolesAsync_SecurityIntent_ReturnsSecurity()
    {
        var compiler = Create();
        var roles = await compiler.AssignRolesAsync("security vulnerability audit", default);
        Assert.Contains(roles, r => r.Name == "security");
    }

    [Fact]
    public async Task AssignRolesAsync_MultiRoleIntent_ReturnsMultiple()
    {
        var compiler = Create();
        var roles = await compiler.AssignRolesAsync("设计、编码并测试一个功能", default);
        Assert.Contains(roles, r => r.Name == "planner");
        Assert.Contains(roles, r => r.Name == "coder");
        Assert.Contains(roles, r => r.Name == "tester");
    }

    [Fact]
    public async Task AssignRolesAsync_UnrecognizedIntent_ReturnsAssistant()
    {
        var compiler = Create();
        var roles = await compiler.AssignRolesAsync("你好", default);
        Assert.Single(roles);
        Assert.Equal("assistant", roles[0].Name);
    }

    [Fact]
    public async Task DesignStructureAsync_SingleRole_OneNodeNoEdges()
    {
        var compiler = Create();
        var roles = new List<AgentRole> { new("coder", "coding", ["file"]) };
        var ir = await compiler.DesignStructureAsync(roles, "write code", default);

        Assert.Single(ir.Nodes);
        Assert.Empty(ir.Edges);
        Assert.Equal("coder", ir.Nodes[0].AgentName);
    }

    [Fact]
    public async Task DesignStructureAsync_ThreeRoles_LinearChain()
    {
        var compiler = Create();
        var roles = new List<AgentRole>
        {
            new("planner", "planning", []),
            new("coder", "coding", ["file"]),
            new("tester", "testing", ["run_command"]),
        };
        var ir = await compiler.DesignStructureAsync(roles, "full pipeline", default);

        Assert.Equal(3, ir.Nodes.Count);
        Assert.Equal(2, ir.Edges.Count);

        Assert.Equal("planner", ir.Nodes[0].AgentName);
        Assert.Equal("coder", ir.Nodes[1].AgentName);
        Assert.Equal("tester", ir.Nodes[2].AgentName);

        Assert.Equal("planner", ir.Edges[0].From);
        Assert.Equal("coder", ir.Edges[0].To);

        Assert.Equal("coder", ir.Edges[1].From);
        Assert.Equal("tester", ir.Edges[1].To);
    }

    [Fact]
    public async Task DesignStructureAsync_EmptyRoles_EmptyGraph()
    {
        var compiler = Create();
        var ir = await compiler.DesignStructureAsync([], "nothing", default);

        Assert.Empty(ir.Nodes);
        Assert.Empty(ir.Edges);
    }

    [Fact]
    public async Task CompileAsync_FullPipeline_EndToEnd()
    {
        var compiler = Create();
        var ir = await compiler.CompileAsync("设计、编码并测试", default);

        Assert.NotNull(ir);
        Assert.NotEmpty(ir.Nodes);
        Assert.NotEmpty(ir.Edges);
        Assert.Contains("设计", ir.Name);
    }

    [Fact]
    public async Task CompileAsync_SimpleTask_NoEdgeCaseCrash()
    {
        var compiler = Create();
        var ir = await compiler.CompileAsync("hello", default);

        Assert.NotNull(ir);
        Assert.Single(ir.Nodes);
        Assert.Equal("assistant", ir.Nodes[0].AgentName);
    }

    [Fact]
    public async Task CompileAsync_LongIntent_SanitizesName()
    {
        var compiler = Create();
        var longIntent = new string('x', 100);
        var ir = await compiler.CompileAsync(longIntent, default);

        Assert.True(ir.Name.Length <= 40);
    }
}
