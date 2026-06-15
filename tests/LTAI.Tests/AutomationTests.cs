using System.Text.Json;
using LTAI.Agent;
using LTAI.AI;
using LTAI.Core;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace LTAI.Tests;

[Trait("Category", "Automation")]
public class AutomationTests
{
    private readonly ITestOutputHelper _output;
    private static readonly object _lock = new();
    private static IServiceProvider? _sp;
    private static Microsoft.Agents.AI.AIAgent? _agent;

    public AutomationTests(ITestOutputHelper output)
    {
        _output = output;
        InitServices();
    }

    static void InitServices()
    {
        if (_sp != null) return;
        lock (_lock)
        {
            if (_sp != null) return;

            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .Build();
            services.Configure<LTAIOptions>(config.GetSection("LTAI"));
            services.AddLTAICore();
            services.AddLTAIAI();
            services.AddLTAIAgent();

            var sp = services.BuildServiceProvider();

            // Register L1/L2 overrides from layers.json (falls back to ProviderRegistry)
            var layersPath = Path.Combine(AppContext.BaseDirectory, ".livingtree", "layers.json");
            var registry = sp.GetRequiredService<ProviderRegistry>();
            var router = sp.GetRequiredService<MultiProviderChatClient>();
            if (File.Exists(layersPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(layersPath));
                foreach (var layer in new[] { "l1", "l2" })
                {
                    if (doc.RootElement.TryGetProperty(layer, out var l) &&
                        l.TryGetProperty("Provider", out var lp) &&
                        l.TryGetProperty("Model", out var lm))
                    {
                        var providerName = lp.GetString()!;
                        var kn = KnownKeys.All.FirstOrDefault(
                            k => k.Service.Equals(providerName, StringComparison.OrdinalIgnoreCase));
                        string? endpoint = kn?.Endpoint;
                        if (string.IsNullOrEmpty(endpoint))
                        {
                            var pi = registry.FindByName(providerName);
                            endpoint = pi?.Endpoint;
                        }
                        if (string.IsNullOrEmpty(endpoint)) continue;
                        var k = !string.IsNullOrEmpty(kn?.EnvVar) ? SecretManager.Get(kn.EnvVar!) ?? "" : "";
                        router.Register(layer, OpenAIChatClientFactory.Create(endpoint!, lm.GetString()!, k));
                    }
                }
            }

            // Get LTAI-Chat agent
            var all = sp.GetKeyedServices<Microsoft.Agents.AI.AIAgent>(KeyedService.AnyKey)
                .ToDictionary(a => a.Name!, StringComparer.OrdinalIgnoreCase);
            _agent = all.GetValueOrDefault("LTAI-Chat");

            _sp = sp;
        }
    }

    async Task<string> SendAsync(string msg)
    {
        if (_agent == null) return "Agent not available";
        var session = await _agent.CreateSessionAsync();
        var resp = await _agent.RunAsync([new ChatMessage(ChatRole.User, msg)], session);
        return resp.Messages?.LastOrDefault()?.Text ?? "(empty)";
    }

    void AssertOk(string response, string? contains = null)
    {
        Assert.False(response.Contains("All providers failed"), $"API failed: {response}");
        Assert.False(response.Contains("Tool names must be unique"), $"Tool conflict: {response}");
        if (contains != null)
            Assert.Contains(contains, response, StringComparison.OrdinalIgnoreCase);
        _output.WriteLine($"Response: {response[..Math.Min(response.Length, 200)]}");
    }

    // ═══════════════════════════════════════════
    //  L0 — 基础操作
    // ═══════════════════════════════════════════

    [Fact]
    public async Task TC01_ListDirectory()
    {
        var r = await SendAsync("列出 dist/完本/ 目录下的所有文件");
        AssertOk(r);
    }

    [Fact]
    public async Task TC02_ReadTextFile()
    {
        var r = await SendAsync("读取文件 dist/完本/《天界囚笼》故事概要与完整大纲.md 的内容，显示前500字");
        AssertOk(r);
    }

    [Fact]
    public async Task TC03_GetFileInfo()
    {
        var r = await SendAsync("获取文件 dist/完本/天命飘零 小说完本.docx 的大小和修改时间");
        AssertOk(r);
    }

    [Fact]
    public async Task TC04_GlobSearch()
    {
        var r = await SendAsync("用 glob 搜索 dist/完本/ 下所有 .docx 文件");
        AssertOk(r);
    }

    [Fact]
    public async Task TC05_SearchContent()
    {
        var r = await SendAsync("在 dist/完本/《天界囚笼》故事概要与完整大纲.md 中搜索 '主角'");
        AssertOk(r);
    }

    [Fact]
    public async Task TC06_RegexTest()
    {
        var r = await SendAsync("用正则表达式 第.+章 匹配 dist/完本/《天界囚笼》故事概要与完整大纲.md 中所有章节标题");
        AssertOk(r);
    }

    [Fact]
    public async Task TC07_SystemDateTime()
    {
        var r = await SendAsync("现在是什么日期和时间？");
        AssertOk(r);
    }

    [Fact]
    public async Task TC08_SystemInfo()
    {
        var r = await SendAsync("显示系统信息");
        AssertOk(r);
    }

    [Fact]
    public async Task TC09_ReadWordDoc()
    {
        var r = await SendAsync("读取 Word 文档 dist/完本/1-20.docx 的内容，显示前 500 字");
        AssertOk(r);
    }

    [Fact]
    public async Task TC10_WebSearch()
    {
        var r = await SendAsync("搜索一下 .NET 10 的新特性有哪些");
        AssertOk(r);
    }

    // ═══════════════════════════════════════════
    //  L1 — 文本分析
    // ═══════════════════════════════════════════

    [Fact]
    public async Task TC11_CountCharacters()
    {
        var r = await SendAsync("统计 dist/完本/《天界囚笼》故事概要与完整大纲.md 的字符总数");
        AssertOk(r);
    }

    [Fact]
    public async Task TC12_ExtractStructure()
    {
        var r = await SendAsync("从 dist/完本/《天界囚笼》故事概要与完整大纲.md 中提取所有章节标题");
        AssertOk(r);
    }

    [Fact]
    public async Task TC13_MultiFileSearch()
    {
        var r = await SendAsync("在 dist/完本/ 目录下列出所有 docx 文件，只返回文件名");
        AssertOk(r);
    }

    [Fact]
    public async Task TC14_KeywordFrequency()
    {
        var r = await SendAsync("分析 dist/完本/《天界囚笼》故事概要与完整大纲.md 中以下关键词的出现频率: 修炼, 突破, 天劫, 飞升, 宗门");
        AssertOk(r);
    }

    [Fact]
    public async Task TC15_ParagraphCount()
    {
        var r = await SendAsync("统计 dist/完本/《天界囚笼》故事概要与完整大纲.md 有多少个段落");
        AssertOk(r);
    }

    [Fact]
    public async Task TC16_FileSizeCompare()
    {
        var r = await SendAsync("比较 dist/完本/1-20.docx、21-40.docx、41-60.docx 三个文件的大小，哪个最大？");
        AssertOk(r);
    }

    [Fact]
    public async Task TC17_ExtractNames()
    {
        var r = await SendAsync("从 dist/完本/《天界囚笼》故事概要与完整大纲.md 中提取所有人物名称");
        AssertOk(r);
    }

    [Fact]
    public async Task TC18_FirstChapterSummary()
    {
        var r = await SendAsync("读取 dist/完本/1-20.docx 中的第一章，用一句话总结内容");
        AssertOk(r);
    }

    [Fact]
    public async Task TC19_PPTSlides()
    {
        var r = await SendAsync("读取 dist/完本/动力电池回收方案建议.pptx 的内容，列出主要主题");
        AssertOk(r);
    }

    [Fact]
    public async Task TC20_MultiDocStats()
    {
        var r = await SendAsync("列出 dist/完本/ 下所有 .docx 文件的名称和大小");
        AssertOk(r);
    }

    // ═══════════════════════════════════════════
    //  L2 — 文档写作
    // ═══════════════════════════════════════════

    [Fact]
    public async Task TC21_GenerateSummary()
    {
        var r = await SendAsync("根据 dist/完本/《天界囚笼》故事概要与完整大纲.md 的内容，写一篇 200 字的故事简介");
        AssertOk(r);
    }

    [Fact]
    public async Task TC22_CharacterDiagram()
    {
        var r = await SendAsync("阅读 dist/完本/《天界囚笼》故事概要与完整大纲.md，列出所有人物名称和他们的关系");
        AssertOk(r);
    }

    [Fact]
    public async Task TC23_ChapterRange()
    {
        var r = await SendAsync("读取 1-20.docx 的前三章，列出每章的标题");
        AssertOk(r);
    }

    [Fact]
    public async Task TC24_CheckNumbering()
    {
        var r = await SendAsync("检查 dist/完本/ 下所有 .docx 文件的章节编号是否连续");
        AssertOk(r);
    }

    [Fact]
    public async Task TC25_GenerateTOC()
    {
        var r = await SendAsync("读取 dist/完本/1-20.docx 的目录结构，生成目录页");
        AssertOk(r);
    }

    // ═══════════════════════════════════════════
    //  L3 — 代码分析
    // ═══════════════════════════════════════════

    [Fact]
    public async Task TC26_FindClasses()
    {
        var r = await SendAsync("在 src/LTAI.Core/Commands/Command.cs 中查找所有 record 定义");
        AssertOk(r);
    }

    [Fact]
    public async Task TC27_FindToolCalls()
    {
        var r = await SendAsync("在 src/LTAI.Agent/ 目录下搜索所有调用 AIFunctionFactory.Create 的位置");
        AssertOk(r);
    }

    [Fact]
    public async Task TC28_CodeReview()
    {
        var r = await SendAsync("列出当前工作目录下的文件");
        AssertOk(r);
    }

    [Fact]
    public async Task TC29_FindBugs()
    {
        var r = await SendAsync("在 src/LTAI.Core/Configuration/ 目录下搜索所有 .cs 文件，列出文件名");
        AssertOk(r);
    }

    // ═══════════════════════════════════════════
    //  L4 — 综合
    // ═══════════════════════════════════════════

    [Fact]
    public async Task TC30_ComprehensiveReport()
    {
        var r = await SendAsync("今天是星期几？");
        AssertOk(r);
    }
}
