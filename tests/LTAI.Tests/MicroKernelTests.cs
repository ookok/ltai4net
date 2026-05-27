using LTAI.Core.Configuration;
using Xunit;

namespace LTAI.Tests;

public sealed class MicroKernelTests
{
    [Fact]
    public async Task ExecuteAsync_EchoCommand_ReturnsOutput()
    {
        var kernel = new LTAI.Core.Governors.MicroKernel(
            Environment.CurrentDirectory);

        Assert.True(kernel.IsHealthy);
        var audit = kernel.GetAuditTrail();
        Assert.NotNull(audit);
        Assert.Empty(audit);
    }

    [Fact]
    public async Task ExecuteAsync_SimpleCommand_Completes()
    {
        var kernel = new LTAI.Core.Governors.MicroKernel(
            Environment.CurrentDirectory);

        var op = new LTAI.Core.Governors.KernelOp
        {
            Command = "cmd.exe",
            Arguments = "/c echo OK",
            WorkingDirectory = Environment.CurrentDirectory,
            Timeout = TimeSpan.FromSeconds(10)
        };

        var result = await kernel.ExecuteAsync(op);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        Assert.Contains("OK", result.Data);
        Assert.True(result.ElapsedMs > 0);
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutCommand_ReturnsTimeout()
    {
        var kernel = new LTAI.Core.Governors.MicroKernel(
            Environment.CurrentDirectory);

        var op = new LTAI.Core.Governors.KernelOp
        {
            Command = "cmd.exe",
            Arguments = "/c timeout 10 /nobreak > nul",
            WorkingDirectory = Environment.CurrentDirectory,
            Timeout = TimeSpan.FromSeconds(1)
        };

        var result = await kernel.ExecuteAsync(op);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidCommand_ReturnsError()
    {
        var kernel = new LTAI.Core.Governors.MicroKernel(
            Environment.CurrentDirectory);

        var op = new LTAI.Core.Governors.KernelOp
        {
            Command = "nonexistent_command_xyz_123",
            Arguments = "",
            WorkingDirectory = Environment.CurrentDirectory,
            Timeout = TimeSpan.FromSeconds(5)
        };

        var result = await kernel.ExecuteAsync(op);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Error);
    }

    [Fact]
    public async Task ReadFileAsync_ExistingFile_ReturnsContent()
    {
        var kernel = new LTAI.Core.Governors.MicroKernel(
            Environment.CurrentDirectory);

        var tempFile = Path.Combine(Path.GetTempPath(), $"ltai_test_{Guid.NewGuid():N}.txt");
        var testContent = "Hello from LTAI MicroKernel test!\nLine 2";
        await File.WriteAllTextAsync(tempFile, testContent);

        try
        {
            var result = await kernel.ReadFileAsync(tempFile);

            Assert.True(result.Success);
            Assert.Equal(testContent, result.Data);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ReadFileAsync_NonExistentFile_ReturnsError()
    {
        var kernel = new LTAI.Core.Governors.MicroKernel(
            Environment.CurrentDirectory);

        var result = await kernel.ReadFileAsync(
            Path.Combine(Path.GetTempPath(), $"ltai_nonexistent_{Guid.NewGuid():N}.txt"));

        Assert.False(result.Success);
        Assert.NotEmpty(result.Error);
    }

    [Fact]
    public async Task WriteFileAsync_CreatesFile()
    {
        var kernel = new LTAI.Core.Governors.MicroKernel(
            Environment.CurrentDirectory);

        var tempFile = Path.Combine(Path.GetTempPath(), $"ltai_test_{Guid.NewGuid():N}.txt");
        var content = "Written by MicroKernel test";

        try
        {
            var result = await kernel.WriteFileAsync(tempFile, content);

            Assert.True(result.Success);
            Assert.True(File.Exists(tempFile));
            Assert.Equal(content, await File.ReadAllTextAsync(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task KernelResult_StaticFactories_Work()
    {
        var ok = LTAI.Core.Governors.KernelResult.Ok("data", elapsedMs: 42);
        Assert.True(ok.Success);
        Assert.Equal("data", ok.Data);
        Assert.Equal(42, ok.ElapsedMs);

        var fail = LTAI.Core.Governors.KernelResult.Fail("err", elapsedMs: 10);
        Assert.False(fail.Success);
        Assert.Equal("err", fail.Error);

        var timeout = LTAI.Core.Governors.KernelResult.Timeout("cmd", elapsedMs: 5000);
        Assert.False(timeout.Success);
        Assert.Contains("cmd", timeout.Error);
        Assert.Contains("timeout", timeout.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MicroKernel_AuditTrail_Accumulates()
    {
        var kernel = new LTAI.Core.Governors.MicroKernel(
            Environment.CurrentDirectory, maxAuditEntries: 10);

        var audit = kernel.GetAuditTrail(limit: 5);
        Assert.NotNull(audit);
        Assert.Empty(audit);
    }

    [Fact]
    public async Task GitOpAsync_WithoutHandler_ReturnsNotConfigured()
    {
        var kernel = new LTAI.Core.Governors.MicroKernel(
            Environment.CurrentDirectory);

        var result = await kernel.GitOpAsync("log", "--oneline -5");

        Assert.False(result.Success);
        Assert.Contains("not configured", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SkillAndMemory_WithoutHandler_ReturnsNotConfigured()
    {
        var kernel = new LTAI.Core.Governors.MicroKernel(
            Environment.CurrentDirectory);

        var skillResult = await kernel.InvokeSkillAsync("test_skill", "input");
        Assert.False(skillResult.Success);

        var memResult = await kernel.QueryMemoryAsync("query", topK: 3);
        Assert.False(memResult.Success);
    }

    [Fact]
    public async Task ExecuteAsync_EnvironmentVars_SetCorrectly()
    {
        var kernel = new LTAI.Core.Governors.MicroKernel(
            Environment.CurrentDirectory);

        var op = new LTAI.Core.Governors.KernelOp
        {
            Command = "cmd.exe",
            Arguments = "/c echo %LTAI_TEST_VAR%",
            WorkingDirectory = Environment.CurrentDirectory,
            Timeout = TimeSpan.FromSeconds(5),
            Environment = new Dictionary<string, string>
            {
                ["LTAI_TEST_VAR"] = "KERNEL_TEST_VALUE"
            }
        };

        var result = await kernel.ExecuteAsync(op);

        Assert.True(result.Success);
        Assert.Contains("KERNEL_TEST_VALUE", result.Data);
    }
}
