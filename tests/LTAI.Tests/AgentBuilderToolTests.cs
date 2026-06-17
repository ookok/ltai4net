using System.Reflection;
using LTAI.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LTAI.Tests;

public class AgentBuilderToolTests
{
    private static readonly Type s_builderType = LoadBuilderType();

    private static Type LoadBuilderType()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "LTAI.Agent")
            ?? Assembly.Load("LTAI.Agent");
        return assembly.GetType("LTAI.Agent.AgentBuilder")!;
    }

    private static List<AITool> InvokeRegister(string methodName, object[] args)
    {
        var tools = new ToolSet();
        var allArgs = new object[] { tools }.Concat(args).ToArray();
        var method = s_builderType.GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method.Invoke(null, allArgs);
        return tools.ToList();
    }

    [Fact]
    public void RegisterBuiltInTools_ContainsKillProcess()
    {
        var result = LTAI.Agent.Tools.SystemTools.ListProcesses();
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public void RegisterShellTools_ContainsPwsh()
    {
        var tools = InvokeRegister("RegisterFileAndTextTools",
            ["LTAI-Chat", false, false, false, true, ".", null!, null!]);

        Assert.Contains(tools, t => t.Name == "RunCommand");
    }

    [Fact]
    public void RegisterFileTools_ContainsRead()
    {
        var tools = InvokeRegister("RegisterFileAndTextTools",
            ["LTAI-Chat", true, false, false, false, ".", null!, null!]);

        Assert.Contains(tools, t => t.Name == "ReadFileContent");
    }

    [Fact]
    public void RegisterSearchTools_ContainsWebSearch()
    {
        var tools = InvokeRegister("RegisterWebTools",
            ["LTAI-Chat", null!, null]);

        Assert.Contains(tools, t => t.Name == "WebSearch");
    }
}

file static class AgentBuilderToolTestHelpers
{
    internal static object[] InvokeRegister(string methodName, params object[] args)
    {
        var type = typeof(LTAI.AI.ToolRegistry);
        var method = type.GetMethod(methodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = method!.Invoke(null, args);
        return (object[])result!;
    }
}
