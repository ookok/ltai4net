using Xunit;
using LTAI.Core.Configuration;
using LTAI.Core.Safety;
using Microsoft.Extensions.Options;

namespace LTAI.Tests;

/// <summary>
/// P0: Production readiness tests — cover the core LLM routing, agent isolation,
/// configuration validation, secret management, and health check paths.
/// These tests do NOT require real API keys or external services.
/// </summary>
public class ProductionReadinessTests
{
    // ════════════════════════════════════════════
    //  SecretManager (P0 encryption)
    // ════════════════════════════════════════════

    [Fact]
    public void SecretManager_SetAndGet_Roundtrip()
    {
        var key = "LTAI_PROD_TEST_" + Guid.NewGuid().ToString("N")[..8];
        SecretManager.Set(key, "sk-test-value", persistent: false);
        Assert.Equal("sk-test-value", SecretManager.Get(key));
        SecretManager.Invalidate(key);
    }

    [Fact]
    public void SecretManager_MissingKey_ReturnsNull()
    {
        Assert.Null(SecretManager.Get("LTAI_NONEXISTENT_KEY_" + Guid.NewGuid().ToString("N")[..8]));
    }

    [Fact]
    public void SecretManager_Has_DetectsPresence()
    {
        var key = "LTAI_PROD_HAS_" + Guid.NewGuid().ToString("N")[..8];
        Assert.False(SecretManager.Has(key));
        SecretManager.Set(key, "present", persistent: false);
        Assert.True(SecretManager.Has(key));
        SecretManager.Invalidate(key);
    }

    // ════════════════════════════════════════════
    //  LTAIOptionsValidator (P0 startup validation)
    // ════════════════════════════════════════════

    [Fact]
    public void OptionsValidator_ValidConfig_Passes()
    {
        var validator = new LTAIOptionsValidator();
        var options = new LTAIOptions
        {
            AI = new AIConfig
            {
                DefaultProvider = "deepseek",
                MaxTokens = 4096,
                Temperature = 0.7,
                GlobalTokenBudget = 1_000_000,
                PerUserTokenBudget = 200_000,
            },
            Web = new WebConfig { Port = 5100 },
            MaxHistoryMessages = 200,
            DataDirectory = ".livingtree",
        };
        var result = validator.Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void OptionsValidator_EmptyDefaultProvider_Fails()
    {
        var validator = new LTAIOptionsValidator();
        var options = new LTAIOptions
        {
            AI = new AIConfig { DefaultProvider = "", MaxTokens = 4096, Temperature = 0.7,
                GlobalTokenBudget = 1_000_000, PerUserTokenBudget = 200_000 },
            Web = new WebConfig { Port = 5100 },
            MaxHistoryMessages = 200,
        };
        var result = validator.Validate(null, options);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void OptionsValidator_InvalidPort_Fails()
    {
        var validator = new LTAIOptionsValidator();
        var options = new LTAIOptions
        {
            AI = new AIConfig { DefaultProvider = "deepseek", MaxTokens = 4096, Temperature = 0.7,
                GlobalTokenBudget = 1_000_000, PerUserTokenBudget = 200_000 },
            Web = new WebConfig { Port = 99999 },
            MaxHistoryMessages = 200,
        };
        var result = validator.Validate(null, options);
        Assert.False(result.Succeeded);
    }

    // ════════════════════════════════════════════
    //  Safety rules (P0: guardrails)
    // ════════════════════════════════════════════

    [Fact]
    public void SafetyRules_ApiKeyInOutput_Blocked()
    {
        // Same format as SafetyRulesTests.ApiKey_Blocked (known to match ApiKeyRx)
        Assert.False(SafetyRules.IsSafeByRules("my api_key abc123def456ghi789jkl"));
    }

    [Fact]
    public void SafetyRules_NormalContent_Passes()
    {
        Assert.True(SafetyRules.IsSafeByRules("How do I implement binary search in C#?"));
    }

    // ════════════════════════════════════════════
    //  LTAIOptions AI layer config (P0: L1/L2)
    // ════════════════════════════════════════════

    [Fact]
    public void AIConfig_GetLayerConfig_L1_ReturnsFlash()
    {
        var config = new AIConfig
        {
            DefaultProvider = "deepseek",
            Model = "deepseek-v4-flash",
            Providers = new()
            {
                ["deepseek-fast"] = new ProviderConfig { Model = "deepseek-v4-flash", Endpoint = "https://api.deepseek.com/v1" },
                ["deepseek-pro"] = new ProviderConfig { Model = "deepseek-v4-pro", Endpoint = "https://api.deepseek.com/v1" },
            }
        };
        var l1 = config.GetLayerConfig("l1");
        Assert.Equal("deepseek-v4-flash", l1.Model);
    }

    [Fact]
    public void AIConfig_GetLayerConfig_L2_ReturnsPro()
    {
        var config = new AIConfig
        {
            DefaultProvider = "deepseek",
            Providers = new()
            {
                ["deepseek-fast"] = new ProviderConfig { Model = "deepseek-v4-flash" },
            }
        };
        var l2 = config.GetLayerConfig("l2");
        Assert.Equal("deepseek-v4-pro", l2.Model);
    }

    // ════════════════════════════════════════════
    //  Embedding config (P0: quantization defaults)
    // ════════════════════════════════════════════

    [Fact]
    public void EmbeddingConfig_GetQuantizationFor_DefaultIsAuto()
    {
        var config = new EmbeddingConfig();
        Assert.Equal("auto", config.GetQuantizationFor("minilm-l6-v2"));
    }

    [Fact]
    public void EmbeddingConfig_GetQuantizationFor_PerModelOverride()
    {
        var config = new EmbeddingConfig
        {
            Quantization = "fp32",
            Models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["minilm-l6-v2"] = "int8"
            }
        };
        Assert.Equal("int8", config.GetQuantizationFor("minilm-l6-v2"));
        Assert.Equal("fp32", config.GetQuantizationFor("bge-small-zh"));
    }

    // ════════════════════════════════════════════
    //  CircuitBreakerStore (P0: SQLite persistence)
    // ════════════════════════════════════════════

    [Fact]
    public async Task CircuitBreakerStore_SaveAndLoad_Roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ltai-cb-{Guid.NewGuid():N}.db");
        var store = new LTAI.Core.Configuration.CircuitBreakerStore(path);
        try
        {
            await store.SaveAsync("test-provider", 3, DateTime.UtcNow.AddSeconds(30));
            var (failures, cooldown) = await store.LoadAsync("test-provider");
            Assert.Equal(3, failures);
            Assert.NotNull(cooldown);
            Assert.True(cooldown.Value > DateTime.UtcNow);

            await store.ClearAsync("test-provider");
            var (f2, _) = await store.LoadAsync("test-provider");
            Assert.Equal(0, f2);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public async Task CircuitBreakerStore_LoadAll_ReturnsAll()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ltai-cb2-{Guid.NewGuid():N}.db");
        var store = new LTAI.Core.Configuration.CircuitBreakerStore(path);
        try
        {
            await store.SaveAsync("p1", 1, null);
            await store.SaveAsync("p2", 5, DateTime.UtcNow.AddMinutes(1));
            var all = await store.LoadAllAsync();
            Assert.Equal(2, all.Count);
            Assert.True(all.ContainsKey("p1"));
            Assert.True(all.ContainsKey("p2"));
        }
        finally
        {
            store.Dispose();
        }
    }
}
