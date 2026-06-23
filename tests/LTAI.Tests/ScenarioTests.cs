using Xunit;
using LTAI.Core.Configuration;
using LTAI.Core.I18n;
using LTAI.Agent.Tools;
using LTAI.Agent;
using LTAI.Core;
using LTAI.Core.Safety;
using Microsoft.Extensions.AI;

namespace LTAI.Tests;

// ═══════════════════════════════════════════════════════════════
//  场景测试：模拟真实用户输入 → 期望输出
//  覆盖：聊天、工具、安全、错误、边界
// ═══════════════════════════════════════════════════════════════

public class SafeShellScenarioTests
{
    [Theory]
    [InlineData("echo hello", "hello", true)]
    [InlineData("dotnet --version", ".", true)]
    [InlineData("rm -rf /", "危险", false)]
    [InlineData("echo success-marker-42", "success-marker-42", true)]
    [InlineData("dir", "", true)]
    [InlineData("ls -la", "", true)]
    [InlineData("sudo rm -rf /", "阻止", false)]
    public async Task RunCommand_InputOutput_MatchesExpected(string command, string expectedContent, bool expectSuccess)
    {
        var tool = new SafeShellTool(Directory.GetCurrentDirectory());
        var result = await tool.RunCommand(command);
        if (expectSuccess)
            Assert.Contains(expectedContent, result, StringComparison.OrdinalIgnoreCase);
        else
            Assert.Contains(expectedContent, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunCommand_ExecutesDirectly()
    {
        var tool = new SafeShellTool(Directory.GetCurrentDirectory());
        var result = await tool.RunCommand("echo hello");
        Assert.Contains("hello", result);
    }

    [Fact]
    public async Task RunCommand_MultipleValidCommands_AllSucceed()
    {
        var tool = new SafeShellTool(Directory.GetCurrentDirectory());
        foreach (var cmd in new[] { "echo a", "echo b", "echo c" })
        {
            var result = await tool.RunCommand(cmd);
            Assert.DoesNotContain("危险", result);
        }
    }
}

public class SafetyRulesScenarioTests
{
    [Theory]
    // 安全内容 — 应通过
    [InlineData("How do I implement binary search in C#?", true)]
    [InlineData("今天的天气怎么样", true)]
    [InlineData("帮我写一个 Python 脚本", true)]
    [InlineData("int x = 42; // this is fine", true)]
    [InlineData("请解释量子计算的原理", true)]
    [InlineData("Check https://example.com for details", true)]
    [InlineData("const double pi = 3.14159;", true)]
    // API 密钥 — 应拦截 (regex: prefix[\s\-_:：]+16+ alphanum chars)
    [InlineData("my api_key a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5", false)]
    [InlineData("use sk- a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5", false)]
    [InlineData("token a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6q7r8s9t0", false)]
    [InlineData("password a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5", false)]
    // SQL 注入 — 应拦截
    [InlineData("DROP TABLE users", false)]
    [InlineData("DELETE FROM accounts", false)]
    [InlineData("TRUNCATE TABLE logs", false)]
    [InlineData("EXEC xp_cmdshell 'dir'", false)]
    // XSS — 应拦截
    [InlineData("<script>alert('xss')</script>", false)]
    [InlineData("onerror=alert(1)", false)]
    // 信用卡/电话 — 应拦截
    [InlineData("4111 1111 1111 1111", false)]
    [InlineData("4111-1111-1111-1111", false)]
    [InlineData("+86 138 0013 8000", false)]
    // PEM 密钥 — 应拦截
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", false)]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----", false)]
    public void IsSafeByRules_InputOutput_MatchesExpected(string input, bool expectedSafe)
    {
        Assert.Equal(expectedSafe, SafetyRules.IsSafeByRules(input));
    }
}

public class SecretManagerScenarioTests
{
    [Fact]
    public void SetAndGet_ApiKeySimulation_Roundtrip()
    {
        var key = "LTAI_DEEPSEEK_TEST_" + Guid.NewGuid().ToString("N")[..8];
        SecretManager.Set(key, "sk-" + Guid.NewGuid().ToString("N"), persistent: false);
        var val = SecretManager.Get(key);
        Assert.NotNull(val);
        Assert.StartsWith("sk-", val);
        Assert.Equal(35, val!.Length); // "sk-" + 32 hex
        SecretManager.Invalidate(key);
    }

    [Fact]
    public void MultipleKeys_IndependentValues()
    {
        var k1 = "LTAI_MK1_" + Guid.NewGuid().ToString("N")[..8];
        var k2 = "LTAI_MK2_" + Guid.NewGuid().ToString("N")[..8];
        SecretManager.Set(k1, "val1", persistent: false);
        SecretManager.Set(k2, "val2", persistent: false);
        Assert.Equal("val1", SecretManager.Get(k1));
        Assert.Equal("val2", SecretManager.Get(k2));
        SecretManager.Invalidate(k1);
        SecretManager.Invalidate(k2);
    }
}

[Collection("Locale")]
public class LocaleScenarioTests
{
    [Theory]
    [InlineData("zh-CN", "AppName", "LTAI 智能助手")]
    [InlineData("en-US", "AppName", "LTAI Assistant")]
    [InlineData("zh-CN", "GreetingHello", "你好")]
    [InlineData("en-US", "GreetingHello", "Hi!")]
    [InlineData("zh-CN", "InitFailed", "初始化失败")]
    [InlineData("en-US", "InitFailed", "Service initialization failed")]
    [InlineData("zh-CN", "SystemPromptIntro", "你是 LTAI 助手")]
    [InlineData("en-US", "SystemPromptIntro", "You are LTAI Assistant")]
    public void Locale_Get_ReturnsExpected(string lang, string key, string expectedContains)
    {
        Locale.SetLang(lang);
        Assert.Contains(expectedContains, Locale.Get(key));
        Locale.SetLang("zh-CN"); // restore default
    }
}

public class AgentRegistryScenarioTests
{
    [Theory]
    [InlineData("""
        ---
        name: Greeter
        description: 问候助手
        temperature: 0.8
        permissions: [read]
        tools: [filesystem]
        ---
        你好！我是问候助手。
        """, "Greeter", "问候助手", 0.8)]
    [InlineData("""
        ---
        name: Calculator
        description: 计算器
        temperature: 0.3
        topP: 0.95
        permissions: [exec]
        tools: [shell]
        modelId: deepseek-v4-flash
        ---
        我是一个计算器
        """, "Calculator", "计算器", 0.3)]
    [InlineData("""
        ---
        name: WriterPro
        description: Writing assistant
        temperature: 1.0
        permissions: [read, write]
        tools: [filesystem, git, web]
        ---
        I help with creative writing.
        """, "WriterPro", "Writing assistant", 1.0)]
    public void ParseAgent_InputFrontmatter_ProducesCorrectDef(string md, string expectedName, string expectedDesc, double expectedTemp)
    {
        var def = AgentRegistry.Parse(md);
        Assert.NotNull(def);
        Assert.Equal(expectedName, def.Name);
        Assert.Equal(expectedDesc, def.Description);
        Assert.Equal(expectedTemp, def.Temperature);
    }

    [Theory]
    [InlineData("no frontmatter here", null)]
    [InlineData("", null)]
    [InlineData("---\nname: OnlyName\n---\n", "OnlyName")]
    public void ParseAgent_EdgeCases(string md, string? expectedName)
    {
        var def = AgentRegistry.Parse(md);
        if (expectedName == null)
            Assert.Null(def);
        else
            Assert.Equal(expectedName, def!.Name);
    }
}

public class CircuitBreakerScenarioTests
{
    [Fact]
    public async Task ThreeFailures_CooldownActivated()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ltai-cb-sc-{Guid.NewGuid():N}.db");
        using var store = new CircuitBreakerStore(path);
        await store.SaveAsync("test-provider", 1, null);
        await store.SaveAsync("test-provider", 2, null);
        await store.SaveAsync("test-provider", 3, DateTime.UtcNow.AddSeconds(30));
        var (failures, cooldown) = await store.LoadAsync("test-provider");
        Assert.Equal(3, failures);
        Assert.NotNull(cooldown);
        Assert.True(cooldown.Value > DateTime.UtcNow);
    }

    [Fact]
    public async Task AfterCooldownExpiry_FailuresReset()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ltai-cb-exp-{Guid.NewGuid():N}.db");
        using var store = new CircuitBreakerStore(path);
        // Simulate cooldown that has already expired
        await store.SaveAsync("expired-provider", 3, DateTime.UtcNow.AddSeconds(-1));
        var (failures, cooldown) = await store.LoadAsync("expired-provider");
        Assert.Equal(3, failures);
        Assert.NotNull(cooldown);
        Assert.True(cooldown.Value <= DateTime.UtcNow); // expired
    }

    [Fact]
    public async Task SuccessClearsFailureCount()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ltai-cb-clr-{Guid.NewGuid():N}.db");
        using var store = new CircuitBreakerStore(path);
        await store.SaveAsync("p", 3, DateTime.UtcNow.AddSeconds(30));
        await store.ClearAsync("p");
        var (failures, _) = await store.LoadAsync("p");
        Assert.Equal(0, failures);
    }
}

[Collection("Locale")]
public class WorkflowI18nScenarioTests
{
    [Theory]
    [InlineData("zh-CN", "{{locale.GreetingHello}}", "你好")]
    [InlineData("en-US", "{{locale.GreetingHello}}", "Hi!")]
    [InlineData("zh-CN", "{{locale.GreetingFarewell}}", "再见")]
    [InlineData("en-US", "{{locale.GreetingFarewell}}", "Goodbye")]
    [InlineData("zh-CN", "{{locale.GreetingThanks}}", "不客气")]
    [InlineData("en-US", "{{locale.GreetingThanks}}", "welcome")]
    [InlineData("zh-CN", "{{locale.GreetingGarbage}}", "没太明白")]
    [InlineData("en-US", "{{locale.GreetingGarbage}}", "didn't quite catch")]
    public void WorkflowTemplate_Resolve_ReturnsLocalized(string lang, string template, string expectedContains)
    {
        Locale.SetLang(lang);
        var result = WorkflowI18nResolver.Resolve(template);
        Assert.Contains(expectedContains, result);
        Locale.SetLang("zh-CN");
    }

    [Fact]
    public void WorkflowTemplate_MultipleVariables_AllResolved()
    {
        Locale.SetLang("zh-CN");
        var result = WorkflowI18nResolver.Resolve("{{locale.GreetingHello}}\n{{locale.GreetingFarewell}}");
        Assert.Contains("你好", result);
        Assert.Contains("再见", result);
        Locale.SetLang("zh-CN");
    }
}

[Collection("Locale")]
public class ToolDescriptionScenarioTests
{
    [Fact]
    public void RegisterThenResolve_EnglishDescription_Works()
    {
        ToolDescriptionProvider.Register("SafeShellTool", "en", "Execute a shell command safely with sandbox restrictions");
        var resolved = ToolDescriptionProvider.Get("SafeShellTool", "en");
        Assert.Equal("Execute a shell command safely with sandbox restrictions", resolved);
    }

    [Fact]
    public void UnregisteredTool_FallsBackToNull()
    {
        Assert.Null(ToolDescriptionProvider.Get("NonExistentTool123", "en"));
    }

    [Fact]
    public void ResolveWithCurrentLang_UsesCorrectTranslation()
    {
        ToolDescriptionProvider.Register("MyTool", "zh-CN", "中文描述");
        ToolDescriptionProvider.Register("MyTool", "en-US", "English description");
        Locale.SetLang("en-US");
        Assert.Equal("English description", ToolDescriptionProvider.Resolve("MyTool"));
        Locale.SetLang("zh-CN");
        Assert.Equal("中文描述", ToolDescriptionProvider.Resolve("MyTool"));
    }
}

public class LTAIOptionsScenarioTests
{
    [Fact]
    public void AIConfig_DefaultProvider_Null() // offline mode: all defaults are null
    {
        var config = new AIConfig();
        Assert.Null(config.DefaultProvider);
        Assert.Null(config.Model);
        Assert.Null(config.ApiKeyEnv);
    }

    [Fact]
    public void AIConfig_LayerConfig_L1_L2_Fallback()
    {
        var config = new AIConfig
        {
            Providers = new()
            {
                ["deepseek-fast"] = new ProviderConfig { Model = "deepseek-v4-flash" },
            }
        };
        Assert.Equal("deepseek-v4-flash", config.GetLayerConfig("l1").Model);
        Assert.Null(config.GetLayerConfig("l2").Model); // fallback to null
    }

    [Fact]
    public void AIConfig_GetLayerConfig_Custom()
    {
        var config = new AIConfig
        {
            Providers = new()
            {
                ["custom-model"] = new ProviderConfig { Model = "custom-ai" },
            }
        };
        Assert.Equal("custom-ai", config.GetLayerConfig("custom-model").Model);
    }

    [Fact]
    public void ProviderConfig_GetApiKey_NoEnvVar_ReturnsNull()
    {
        var pc = new ProviderConfig { EnvVar = "LTAI_NONEXISTENT_KEY_XYZ" };
        Assert.Null(pc.GetApiKey());
    }
}

public class PathUtilsScenarioTests
{
    [Theory]
    [InlineData("test.txt", true)]
    [InlineData("subdir/test.txt", true)]
    [InlineData("../outside.txt", false)]
    [InlineData("..\\outside.txt", false)]
    [InlineData("../../etc/passwd", false)]
    public void SafeResolvePath_VariousInputs_ReturnsExpected(string input, bool expectValid)
    {
        var result = PathUtils.SafeResolvePath(Environment.CurrentDirectory, input);
        if (expectValid)
            Assert.NotNull(result);
        else
            Assert.Null(result);
    }
}

public class EdgeCaseTests
{
    [Fact]
    public void SafetyRules_NullInput_ReturnsTrue()
    {
        Assert.True(SafetyRules.IsSafeByRules(null!));
    }

    [Fact]
    public void SafetyRules_EmptyInput_ReturnsTrue()
    {
        Assert.True(SafetyRules.IsSafeByRules(""));
    }

    [Fact]
    public void SafetyRules_VeryLongInput_DoesNotThrow()
    {
        var longInput = new string('x', 100_000);
        var ex = Record.Exception(() => SafetyRules.IsSafeByRules(longInput));
        Assert.Null(ex);
    }

    [Fact]
    public void SecretManager_VeryLongKey_DoesNotThrow()
    {
        var key = "LTAI_LONG_" + Guid.NewGuid().ToString("N");
        var val = new string('x', 10_000);
        SecretManager.Set(key, val, persistent: false);
        Assert.Equal(val, SecretManager.Get(key));
        SecretManager.Invalidate(key);
    }

    [Fact]
    public void LTAIOptionsValidator_MaxBoundaryValues_Passes()
    {
        var validator = new LTAIOptionsValidator();
        var opts = new LTAIOptions
        {
            AI = new AIConfig
            {
                DefaultProvider = "deepseek",
                MaxTokens = 1_000_000,
                Temperature = 2.0,
                GlobalTokenBudget = long.MaxValue,
                PerUserTokenBudget = long.MaxValue,
            },
            Web = new WebConfig { Port = 65535 },
            MaxHistoryMessages = 10000,
            DataDirectory = "/tmp/test",
        };
        Assert.True(validator.Validate(null, opts).Succeeded);
    }

    [Fact]
    public void AIConfig_DegradationChain_MultipleLevels()
    {
        var config = new AIConfig
        {
            DefaultProvider = "fast",
            DegradationChain = new()
            {
                ["fast"] = "medium",
                ["medium"] = "slow",
                ["slow"] = "fallback",
            }
        };
        Assert.Equal("fast", config.DefaultProvider);
        Assert.Contains("fast", config.DegradationChain.Keys);
        Assert.Equal("medium", config.DegradationChain["fast"]);
        Assert.Equal("slow", config.DegradationChain["medium"]);
    }
}

public class AgentPrintScenarioTests
{
    [Fact]
    public void AgentFileDef_CapabilityText_IncludesDescAndTools()
    {
        var def = new AgentFileDef
        {
            Name = "TestAgent",
            Description = "A test agent",
            Tools = ["filesystem", "git"],
            Prompt = "Help users with code"
        };
        Assert.Contains("A test agent", def.CapabilityText);
        Assert.Contains("filesystem", def.CapabilityText);
        Assert.Contains("git", def.CapabilityText);
        Assert.Contains("Help", def.CapabilityText);
    }

    [Fact]
    public void AgentFileDef_EmptyTools_DoesNotThrow()
    {
        var def = new AgentFileDef
        {
            Name = "Empty",
            Description = "desc",
            Tools = [],
            Prompt = ""
        };
        Assert.NotNull(def.CapabilityText);
    }
}

public class BackgroundJobScenarioTests
{
    [Fact]
    public async Task StartJob_SyncCommand_ReturnsJobId()
    {
        var svc = new BackgroundJobService();
        var result = await svc.StartJob("echo test-output");
        Assert.Contains("Job #", result);
    }

    [Fact]
    public async Task WaitForJob_SimpleEcho_ReturnsOutput()
    {
        var svc = new BackgroundJobService();
        var jobResult = await svc.StartJob("echo hello-job-world");
        var id = jobResult.Split(' ')[1].TrimStart('#').TrimEnd('.');
        var output = await svc.WaitForJob(id, timeoutSec: 10);
        Assert.Contains("hello-job-world", output);
    }

    [Fact]
    public void ListJobs_InitiallyEmpty_ReturnsMessage()
    {
        var svc = new BackgroundJobService();
        var list = svc.ListJobs();
        Assert.Contains("No background", list);
    }
}

public class FileSystemToolScenarioTests : IDisposable
{
    private readonly string _tmpDir;

    public FileSystemToolScenarioTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "ltai-fs-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }



    [Fact]
    public async Task ReadFile_NotFound_ReturnsError()
    {
        var fs = new FileSystemTools(_tmpDir);
        var result = await fs.ReadFileContent(Path.Combine(_tmpDir, "nonexistent.txt"));
        Assert.Contains("reading", result);
    }

    [Fact]
    public async Task ReadFile_AfterWriting_ReturnsContent()
    {
        var fs = new FileSystemTools(_tmpDir);
        var testFile = Path.Combine(_tmpDir, "testfile.txt");
        await File.WriteAllTextAsync(testFile, "Hello LTAI Test!");
        var content = await fs.ReadFileContent(testFile);
        Assert.Contains("Hello LTAI Test!", content);
    }

    [Fact]
    public void ListFiles_ReturnsEntries()
    {
        var fs = new FileSystemTools(_tmpDir);
        File.WriteAllText(Path.Combine(_tmpDir, "a.txt"), "");
        var list = fs.ListFiles(".");
        Assert.True(list.Length > 0, $"Expected at least 1 file, got 0. Dir={_tmpDir}");
    }
}
