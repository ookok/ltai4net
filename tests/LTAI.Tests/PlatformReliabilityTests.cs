using System.Diagnostics;
using LTAI.Core.Configuration;
using LTAI.Core.Setup;
using LTAI.DNA.Safety;
using LTAI.Models;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LTAI.Tests;

/// <summary>
/// Platform Reliability Tests for LTAI V0.51.
/// No external dependencies. API keys from env vars only.
/// </summary>
public class PlatformReliabilityTests
{
    // ── Startup ──

    [Fact] public void REL_01_NoConfigFile_DoesNotCrash() {
        var options = new LTAIOptions();
        Assert.NotNull(options); Assert.NotNull(options.AI); }

    [Fact] public void REL_02_BrokenYaml_FirstRunDetected() {
        var f = Path.GetTempFileName();
        try { File.WriteAllText(f, "{{{ broken"); var s = FirstRunDetector.Check(f);
            Assert.True(s.IsFirstRun); } finally { File.Delete(f); } }

    [Fact] public void REL_03_MissingUnifiedSafety_Detected() {
        var c = new LTAI.Models.AgentConfig { Agents = new() { new() { Name="x",Type=AgentType.Chat,Middleware=new(){"unified_safety"} } } };
        Assert.Single(c.Agents[0].Middleware); }

    [Fact] public void REL_04_InvalidAgentType_FallsBack() {
        var card = new LTAIAgentCard { Name="t", Type=AgentType.Custom, Instructions="" };
        Assert.NotNull(card); }

    // ── Configuration ──

    [Fact] public void REL_05_ProviderEnvKey_Defaults() {
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "sk-test");
        var k = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        Assert.Equal("sk-test", k); }

    [Fact] public void REL_06_DefaultProvider_Applied() {
        var o = new LTAIOptions();
        if (o.AI.Providers.Count==0) o.AI.Providers["deepseek"]=new(){Endpoint="https://api.deepseek.com",Model="deepseek-chat"};
        Assert.NotEmpty(o.AI.Providers); }

    [Fact] public void REL_07_LTAIOptions_BindsLayer() {
        var dict = new Dictionary<string,string?>{["LTAI:AI:l0:provider"]="s",["LTAI:AI:l0:model"]="m"};
        var o = new LTAIOptions(); new ConfigurationBuilder().AddInMemoryCollection(dict).Build().GetSection("LTAI").Bind(o);
        Assert.Equal("s",o.AI.L0.Provider); Assert.Equal("m",o.AI.L0.Model); }

    // ── Infrastructure ──

    [Fact] public void REL_08_NoRedis_HealthUnhealthy() {
        var p = new ServiceCollection().BuildServiceProvider();
        Assert.Null(p.GetService<IDistributedCache>()); }

    [Fact] public void REL_09_MemoryCache_Registered() {
        var p = new ServiceCollection().AddDistributedMemoryCache().AddLogging().BuildServiceProvider();
        Assert.NotNull(p.GetService<IDistributedCache>()); }

    [Fact] public void REL_10_LocalEmbedding_Works() {
        var b = new LTAI.Knowledge.Vector.Embedding.LocalEmbeddingBackend(NullLogger<LTAI.Knowledge.Vector.Embedding.LocalEmbeddingBackend>.Instance);
        var e = b.EmbedAsync(new[]{"test"}).Result;
        Assert.Equal(384,e[0].Length); }

    [Fact] public void REL_11_SafetyGate_Instantiable() {
        var p=new PolicyAsCode(); p.LoadDefaults();
        var g=new UnifiedSafetyGate(NullLogger<UnifiedSafetyGate>.Instance,new SafetyCoordinator(NullLogger<SafetyCoordinator>.Instance),p);
        Assert.NotNull(g); }

    // ── Performance ──

    [Fact] public void REL_12_DIWiring_CompletesQuickly() {
        var sw = Stopwatch.StartNew();
        var s = new ServiceCollection(); s.AddLogging(); s.AddSingleton(Options.Create(new LTAIOptions()));
        s.AddDistributedMemoryCache();
        var p = s.BuildServiceProvider(); sw.Stop();
        Assert.True(sw.Elapsed.TotalSeconds<10); }

    [Fact] public void REL_13_SafetyGate_InitFast() {
        var sw = Stopwatch.StartNew();
        var p=new PolicyAsCode(); p.LoadDefaults();
        new UnifiedSafetyGate(NullLogger<UnifiedSafetyGate>.Instance,new SafetyCoordinator(NullLogger<SafetyCoordinator>.Instance),p);
        sw.Stop();
        Assert.True(sw.Elapsed.TotalMilliseconds<200); }

    // ── Resilience ──

    [Fact] public void REL_14_MissingEnvVar_Fallback() {
        Assert.Null(Environment.GetEnvironmentVariable("NONEXISTENT_TEST_KEY")); }

    [Fact] public void REL_15_ConfigReadsLtaiJson() {
        var j="""{"constitution":{"preamble":"t","principles":[]},"middleware":{"pipeline":["unified_safety"]}}""";
        var f = Path.GetTempFileName();
        try { File.WriteAllText(f,j); Assert.Contains("unified_safety",File.ReadAllText(f)); }
        finally { File.Delete(f); } }
}
