using LTAI.Tools.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

public class ToolSynthesizerAuditTests
{
    private readonly ToolSynthesizer _synthesizer = new(NullLogger<ToolSynthesizer>.Instance);

    [Fact]
    public async Task Synthesize_SafeCode_Succeeds()
    {
        var result = await _synthesizer.Synthesize(
            "Calculate the sum of two numbers",
            "math",
            async (role, prompt) => """
                {
                    "name": "add_numbers",
                    "code": "def execute(a, b):\n    return {'result': a + b}",
                    "params": ["a", "b"]
                }
                """);

        Assert.True(result.Success);
        Assert.NotNull(result.Tool);
        Assert.Equal("add_numbers", result.Tool!.Name);
    }

    [Fact]
    public async Task Synthesize_DangerousImport_Blocked()
    {
        var result = await _synthesizer.Synthesize(
            "Execute a system command",
            "shell",
            async (role, prompt) => """
                {
                    "name": "evil_tool",
                    "code": "import os\ndef execute(cmd):\n    return {'result': os.system(cmd)}",
                    "params": ["cmd"]
                }
                """);

        Assert.False(result.Success);
        Assert.Contains("Security audit failed", result.Error);
    }

    [Fact]
    public async Task Synthesize_EvalUsage_Blocked()
    {
        var result = await _synthesizer.Synthesize(
            "Evaluate expression",
            "math",
            async (role, prompt) => """
                {
                    "name": "eval_tool",
                    "code": "def execute(expr):\n    return {'result': eval(expr)}",
                    "params": ["expr"]
                }
                """);

        Assert.False(result.Success);
        Assert.Contains("Security audit failed", result.Error);
    }

    [Fact]
    public async Task Synthesize_SubprocessUsage_Blocked()
    {
        var result = await _synthesizer.Synthesize(
            "Run subprocess",
            "shell",
            async (role, prompt) => """
                {
                    "name": "subprocess_tool",
                    "code": "import subprocess\ndef execute(cmd):\n    return {'result': subprocess.run(cmd)}",
                    "params": ["cmd"]
                }
                """);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Synthesize_NetworkRequest_Blocked()
    {
        var result = await _synthesizer.Synthesize(
            "Make HTTP request",
            "network",
            async (role, prompt) => """
                {
                    "name": "http_tool",
                    "code": "import requests\ndef execute(url):\n    return {'result': requests.get(url).text}",
                    "params": ["url"]
                }
                """);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Synthesize_MissingExecuteFunction_Blocked()
    {
        var result = await _synthesizer.Synthesize(
            "Do something",
            "misc",
            async (role, prompt) => """
                {
                    "name": "bad_tool",
                    "code": "def main(a, b):\n    return {'result': a + b}",
                    "params": ["a", "b"]
                }
                """);

        Assert.False(result.Success);
        Assert.Contains("execute", result.Error);
    }

    [Fact]
    public async Task Synthesize_InvalidJson_Fails()
    {
        var result = await _synthesizer.Synthesize(
            "Do something",
            "misc",
            async (role, prompt) => "This is not valid JSON at all");

        Assert.False(result.Success);
        Assert.Contains("Invalid JSON", result.Error);
    }

    [Fact]
    public void ListTools_ReturnsSynthesizedTools()
    {
        var tools = _synthesizer.ListTools();
        Assert.NotNull(tools);
    }
}
