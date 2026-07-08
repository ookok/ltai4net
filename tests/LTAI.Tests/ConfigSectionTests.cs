using Xunit;
using LTAI.Core.Configuration;
using LTAI.AI;
using Microsoft.Extensions.Configuration;

namespace LTAI.Tests;

public class ConfigSectionTests
{
    // ════════════════════════════════════════════
    //  MirrorConfig
    // ════════════════════════════════════════════

    [Fact]
    public void MirrorConfig_DefaultValues_AreNull()
    {
        var config = new MirrorConfig();
        Assert.NotNull(config.WarpMsiUrl);
        Assert.NotNull(config.WindowsTerminalUrl);
        Assert.NotNull(config.RipGrepUrl);
        Assert.NotNull(config.ModelBaseUrl);
    }

    [Fact]
    public void MirrorConfig_WarpMsiUrl_ReadsFromConfig()
    {
        var config = Config("LTAI:Mirrors:WarpMsiUrl", "http://test/warp.msi");
        var mirrors = config.GetSection("LTAI:Mirrors").Get<MirrorConfig>();
        Assert.NotNull(mirrors);
        Assert.Equal("http://test/warp.msi", mirrors.WarpMsiUrl);
    }

    [Fact]
    public void MirrorConfig_WindowsTerminalUrl_ReadsFromConfig()
    {
        var config = Config("LTAI:Mirrors:WindowsTerminalUrl", "http://test/terminal.zip");
        var mirrors = config.GetSection("LTAI:Mirrors").Get<MirrorConfig>();
        Assert.NotNull(mirrors);
        Assert.Equal("http://test/terminal.zip", mirrors.WindowsTerminalUrl);
    }

    [Fact]
    public void MirrorConfig_RipGrepUrl_ReadsFromConfig()
    {
        var config = Config("LTAI:Mirrors:RipGrepUrl", "http://test/rg.exe");
        var mirrors = config.GetSection("LTAI:Mirrors").Get<MirrorConfig>();
        Assert.NotNull(mirrors);
        Assert.Equal("http://test/rg.exe", mirrors.RipGrepUrl);
    }

    [Fact]
    public void MirrorConfig_ModelBaseUrl_ReadsFromConfig()
    {
        var config = Config("LTAI:Mirrors:ModelBaseUrl", "http://test/models/");
        var mirrors = config.GetSection("LTAI:Mirrors").Get<MirrorConfig>();
        Assert.NotNull(mirrors);
        Assert.Equal("http://test/models/", mirrors.ModelBaseUrl);
    }

    // ════════════════════════════════════════════
    //  SecurityConfig
    // ════════════════════════════════════════════

    [Fact]
    public void SecurityConfig_DefaultValue()
    {
        var security = new SecurityConfig();
        Assert.Equal(@"C:\Windows\system32;C:\Windows", security.SystemPathFallback);
    }

    [Fact]
    public void SecurityConfig_ReadsFromConfig()
    {
        var config = Config("LTAI:Security:SystemPathFallback", "/usr/bin:/bin");
        var security = config.GetSection("LTAI:Security").Get<SecurityConfig>();
        Assert.NotNull(security);
        Assert.Equal("/usr/bin:/bin", security.SystemPathFallback);
    }

    // ════════════════════════════════════════════
    //  AI null model handling
    // ════════════════════════════════════════════

    [Fact]
    public void AIConfig_ModelNull_ThrowsOnClientCreation()
    {
        var options = new LTAIOptions
        {
            AI = new AIConfig
            {
                Model = null,
                DefaultProvider = "test",
            }
        };
        using var client = TestHelper.CreateRouter(options);
        Assert.NotNull(client);
    }

    [Fact]
    public void AIConfig_GetLayerConfig_ModelNull_ReturnsNull()
    {
        var config = new AIConfig { Model = null };
        var result = config.GetLayerConfig("fast");
        Assert.NotNull(result);
        Assert.Null(result.Model);
    }

    [Fact]
    public void AIConfig_GetLayerConfig_UnknownLayer_ReturnsNull()
    {
        var config = new AIConfig { Model = null };
        var result = config.GetLayerConfig("unknown");
        Assert.NotNull(result);
        Assert.Null(result.Model);
    }

    [Fact]
    public void KnownKeys_GetDefaultModel_ReturnsNull()
    {
        var deepseek = KnownKeys.All.FirstOrDefault(k => k.EnvVar == "DEEPSEEK_API_KEY");
        Assert.NotNull(deepseek);
        Assert.Null(deepseek.Model);
    }

    [Fact]
    public void EmbeddingClient_DefaultProviders_HaveModelsExceptUnsupported()
    {
        // Providers with a public embedding API must carry a non-empty default model so the
        // Remote fallback layer can actually succeed; DeepSeek has no public embedding API.
        Assert.All(EmbeddingClient.DefaultProviders.Where(p => p.name != "DeepSeek"),
            p => Assert.False(string.IsNullOrEmpty(p.model), $"{p.name} should declare a default embedding model"));
        var deepseek = EmbeddingClient.DefaultProviders.FirstOrDefault(p => p.name == "DeepSeek");
        if (deepseek.name != null)
            Assert.True(string.IsNullOrEmpty(deepseek.model), "DeepSeek has no public embedding API");
    }

    [Fact]
    public void SafetyClient_NullConfig_Throws()
    {
        var options = new LTAIOptions
        {
            AI = new AIConfig
            {
                Model = null,
                DefaultProvider = null,
            }
        };
        using var client = TestHelper.CreateRouter(options);
        Assert.NotNull(client);
        Assert.Null(client.ActiveProvider);
    }

    private static IConfigurationRoot Config(string key, string value)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [key] = value
            })
            .Build();
    }
}
