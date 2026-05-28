using System.Diagnostics;
using LTAI.AI.Interfaces;
using System.Text;
using LTAI.AI;
using LTAI.AI.Governors;
using LTAI.Agent;
using LTAI.Agent.MAF;
using LTAI.Agent.Tools;
using LTAI.Cli.Debug;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.Core.Messaging;
using LTAI.Core.Setup;
using LTAI.Knowledge.Vector;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LTAI.Knowledge.Core;
using Spectre.Console;

namespace LTAI.Cli;

internal static class DebugMode
{
    private static string DocsDir
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var rootDir = FindRootDirectory(baseDir, "docs");
            return rootDir != null ? Path.Combine(rootDir, "docs", "debug") : Path.Combine(baseDir, "docs", "debug");
        }
    }

    public static async Task RunAsync(string? query, int count, string? difficulty, string? domain, bool generateReport)
    {
        Console.WriteLine("=== LTAI Debug Mode (Live Pipeline) ===\n");

        var baseDir = AppContext.BaseDirectory;
        var configPath = Path.Combine(baseDir, "appsettings.json");

        if (!File.Exists(configPath) || new FileInfo(configPath).Length < 30)
        {
            Console.WriteLine("未检测到配置文件，使用环境变量自动生成...");
            AutoBootstrapConfig(configPath);
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: true, reloadOnChange: false)
            .Build();

        Console.Write("正在初始化服务容器...");
        var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(cfg => cfg.AddJsonFile(configPath, optional: true, reloadOnChange: false))
            .ConfigureServices((ctx, services) =>
            {
                var ltaiOptions = new LTAIOptions();
                ctx.Configuration.GetSection(LTAIOptions.SectionName).Bind(ltaiOptions);
                if (ltaiOptions.AI.Providers.Count == 0)
                {
                    ltaiOptions.AI.Providers["deepseek"] = new ProviderConfig
                    {
                        Endpoint = OptionService.Get("deepseek.endpoint") ?? "https://api.deepseek.com",
                        Model = OptionService.Get("deepseek.model") ?? "deepseek-v4-pro"
                    };
                    ltaiOptions.AI.Providers["deepseek-fast"] = new ProviderConfig
                    {
                        Endpoint = OptionService.Get("deepseek.fast.endpoint") ?? "https://api.deepseek.com",
                        Model = OptionService.Get("deepseek.fast.model") ?? "deepseek-v4-flash"
                    };
                }
                services.AddSingleton(Options.Create(ltaiOptions));

                services.AddLTAICore();
                services.AddLTAIVectorAuto();
                services.AddLTAIAgent();
                services.AddLTAIAI();
            })
            .ConfigureHostOptions(o => o.ServicesStartConcurrently = false)
            .Build();
        Console.WriteLine(" OK");

        // 和 Host 启动方式一致: 创建 scope 再解析
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;

        Console.Write("正在创建 LivingTree 系统...");
        Console.Out.Flush();
        var livingTree = sp.GetRequiredService<ILivingTreeSystem>();
        Console.WriteLine(" OK");

        Console.Write("正在加载配置...");
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LTAIOptions>>().Value;
        Console.WriteLine(" OK");

        Console.Write("正在注册工具...");
        var toolRegistry = sp.GetRequiredService<AIToolRegistry>();
        await toolRegistry.RegisterAllToolCategoriesAsync().ConfigureAwait(false);
        await sp.RegisterMarkdownToolsAsync(toolRegistry).ConfigureAwait(false);
        Console.WriteLine($" OK ({toolRegistry.ListTools().Count()} 个工具)");

        var hasProvider = options.AI.Providers.Any(kv => !string.IsNullOrEmpty(kv.Value.Endpoint));
        if (!hasProvider)
        {
            Console.WriteLine("无可用 AI 提供商");
            return;
        }

        Console.Write("正在初始化 LivingTree...");
        try { await livingTree.InitializeAsync().ConfigureAwait(false); Console.WriteLine(" OK"); }
        catch (Exception ex) { Console.WriteLine($" 失败: {ex.Message}"); return; }

        if (!string.IsNullOrEmpty(query))
        {
            await RunLiveQueryAsync(query, livingTree, options).ConfigureAwait(false);
        }
        else
        {
            await RunBatchTestAsync(count, livingTree, options).ConfigureAwait(false);
        }

        Console.WriteLine("\nDebug mode completed.");
    }

    private static async Task RunLiveQueryAsync(string query, ILivingTreeSystem livingTree, LTAIOptions options)
    {
        var obs = new DebugObservability(livingTree);
        var snapBefore = obs.Snapshot();

        Console.WriteLine($"Query: {query}");
        Console.WriteLine(new string('-', 60));

        var sw = Stopwatch.StartNew();
        var fullResponse = new StringBuilder();

        try
        {
            await foreach (var chunk in livingTree.StreamChatAsync(query))
            {
                AnsiConsole.Markup(Markup.Escape(chunk));
                fullResponse.Append(chunk);
                Console.Out.Flush();
            }
            sw.Stop();

            var response = fullResponse.ToString();
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine(new string('-', 60));
            AnsiConsole.MarkupLine($"[bold]L1:[/] [yellow]{options.AI.L1.Provider}/{options.AI.L1.Model}[/]");
            AnsiConsole.MarkupLine($"[bold]L2:[/] [cyan]{options.AI.L2.Provider}/{options.AI.L2.Model}[/]");
            AnsiConsole.MarkupLine($"[bold]L0:[/] [green]{options.AI.L0.Provider}/{options.AI.L0.Model}[/]");
            AnsiConsole.MarkupLine($"ONNX: {(options.AI.OnnxEnabled ? "[green]enabled[/]" : "[dim]disabled[/]")}  |  {sw.ElapsedMilliseconds}ms");

            var snapAfter = obs.Snapshot();
            Console.WriteLine();
            AnsiConsole.MarkupLine("[bold cyan]── Pipeline Metrics ──[/]");
            PrintMetricChanged(snapBefore, snapAfter, "bavt.budget_ratio", "BAVT预算比", "F3");
            PrintMetricChanged(snapBefore, snapAfter, "erl.total_trials", "ERL总试验", null);
            PrintMetricChanged(snapBefore, snapAfter, "erl.success_rate", "ERL成功率", "F3");
            PrintMetricChanged(snapBefore, snapAfter, "elastic.raw", "弹性记忆(原始)", null);
            PrintMetricChanged(snapBefore, snapAfter, "elastic.compressed", "弹性记忆(压缩)", null);
            PrintMetricChanged(snapBefore, snapAfter, "elastic.episodic", "弹性记忆(情景)", null);
            PrintMetricChanged(snapBefore, snapAfter, "reflection.total", "反射恢复次数", null);
            PrintMetricChanged(snapBefore, snapAfter, "reflection.recovery_rate", "反射恢复率", "F3");
            PrintMetricChanged(snapBefore, snapAfter, "evolution.total_lessons", "跨轮次教训总数", null);
            PrintMetricChanged(snapBefore, snapAfter, "evolution.active_lessons", "活跃教训数", null);
            PrintMetricChanged(snapBefore, snapAfter, "verifiable.measurements", "已验证测量值", null);
            PrintMetricChanged(snapBefore, snapAfter, "verifiable.citations", "已验证引用", null);

            var lessons = snapAfter.GetValueOrDefault("evolution.lessons_prompt")?.ToString();
            if (!string.IsNullOrWhiteSpace(lessons) && lessons != snapBefore.GetValueOrDefault("evolution.lessons_prompt")?.ToString())
            {
                Console.WriteLine();
                Console.WriteLine("── Active Cross-Run Lessons ──");
                Console.WriteLine(lessons);
            }

            var docDir = DocsDir;
            Directory.CreateDirectory(docDir);
            var docPath = Path.Combine(docDir, $"trace_{DateTime.UtcNow:yyyyMMdd_HHmmss}.md");
            await File.WriteAllTextAsync(docPath, GenerateLiveTraceDocument(query, response, sw.ElapsedMilliseconds, options), default).ConfigureAwait(false);
            Console.WriteLine($"\nTrace: {docPath}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"\nERROR: {ex.Message}");
        }
    }

    private static void PrintMetricChanged(Dictionary<string, object> before, Dictionary<string, object> after, string key, string label, string? format)
    {
        var prev = before.GetValueOrDefault(key);
        var curr = after.GetValueOrDefault(key);

        if (prev == null && curr == null) return;

        var prevStr = FormatMetric(prev, format);
        var currStr = FormatMetric(curr, format);
        var delta = prev == null || prev.Equals(curr) ? "" : $"(Δ)";

        Console.WriteLine($"  {label}: {currStr} {delta}");
    }

    private static string FormatMetric(object? val, string? format)
    {
        if (val == null) return "n/a";
        return format switch
        {
            "F2" => string.Format("{0:F2}", val),
            "F3" => string.Format("{0:F3}", val),
            _ => val.ToString() ?? "?"
        };
    }

    private static async Task RunBatchTestAsync(int count, ILivingTreeSystem livingTree, LTAIOptions options)
    {
        var generator = new HeuristicQuestionGenerator();
        Console.WriteLine($"生成 {count} 个测试用例...");
        var tests = generator.GenerateTests(count);
        Console.WriteLine($"  Simple: {tests.Count(t => t.Difficulty == TestDifficulty.Simple)}, Moderate: {tests.Count(t => t.Difficulty == TestDifficulty.Moderate)}, Complex: {tests.Count(t => t.Difficulty == TestDifficulty.Complex)}, OOD: {tests.Count(t => t.Difficulty == TestDifficulty.OOD)}");
        Console.WriteLine("\n运行真实全链路测试...\n");

        var results = new List<(HeuristicTestCase Test, bool Pass, string? Response, long Ms)>();
        for (int i = 0; i < tests.Count; i++)
        {
            var test = tests[i];
            Console.Write($"[{i + 1}/{tests.Count}] [{test.Difficulty}] {Truncate(test.Query, 60).PadRight(60)} ");
            try
            {
                var sw = Stopwatch.StartNew();
                var output = await livingTree.ProcessTypedAsync(GovernorInput.Create(test.Query), default).ConfigureAwait(false);
                sw.Stop();
                var ok = !output.IsBlocked && !string.IsNullOrEmpty(output.Response);
                Console.Write($"{(ok ? "PASS" : "FAIL")} ({sw.ElapsedMilliseconds}ms)");
                results.Add((test, ok, output.Response, sw.ElapsedMilliseconds));
            }
            catch (Exception ex)
            {
                Console.Write($"ERR: {Truncate(ex.Message, 30)}");
                results.Add((test, false, null, 0));
            }
            Console.WriteLine();
        }

        var passed = results.Count(r => r.Pass);
        Console.WriteLine($"\n结果: {passed}/{tests.Count} 通过");
        var reportPath = Path.Combine(DocsDir, $"debug_report_{DateTime.UtcNow:yyyyMMdd_HHmmss}.md");
        await File.WriteAllTextAsync(reportPath, GenerateLiveDebugDocument(results, tests, options)).ConfigureAwait(false);
        Console.WriteLine($"报告: {reportPath}");
    }

    private static string GenerateLiveTraceDocument(string query, string response, long ms, LTAIOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# LTAI Live Trace Report");
        sb.AppendLine();
        sb.AppendLine($"| Item | Value |");
        sb.AppendLine($"|------|-------|");
        sb.AppendLine($"| **Query** | {Truncate(query, 100)} |");
        sb.AppendLine($"| **Time** | {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC |");
        sb.AppendLine($"| **Duration** | {ms}ms |");
        sb.AppendLine($"| **L1** | {options.AI.L1.Provider}/{options.AI.L1.Model} |");
        sb.AppendLine($"| **L2** | {options.AI.L2.Provider}/{options.AI.L2.Model} |");
        sb.AppendLine($"| **L0** | {options.AI.L0.Provider}/{options.AI.L0.Model} |");
        sb.AppendLine($"| **ONNX** | {(options.AI.OnnxEnabled ? "enabled" : "disabled")} |");
        sb.AppendLine();
        sb.AppendLine("## Response");
        sb.AppendLine();
        sb.AppendLine(response);
        sb.AppendLine();
        sb.AppendLine($"---\n*Generated at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC*");
        return sb.ToString();
    }

    private static string GenerateLiveDebugDocument(List<(HeuristicTestCase Test, bool Pass, string? Response, long Ms)> results,
        List<HeuristicTestCase> tests, LTAIOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# LTAI Live Debug Report");
        sb.AppendLine();
        sb.AppendLine($"**Generated:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"**L1:** {options.AI.L1.Provider}/{options.AI.L1.Model}");
        sb.AppendLine($"**L2:** {options.AI.L2.Provider}/{options.AI.L2.Model}");
        sb.AppendLine();
        sb.AppendLine("| # | Difficulty | Pass | Time | Query | Response |");
        sb.AppendLine("|---|------------|------|------|-------|----------|");
        for (int i = 0; i < results.Count; i++)
        {
            var (test, pass, resp, ms) = results[i];
            sb.AppendLine($"| {i + 1} | {test.Difficulty} | {(pass ? "PASS" : "FAIL")} | {ms}ms | {Truncate(test.Query, 40)} | {Truncate(resp, 40)} |");
        }
        sb.AppendLine();
        sb.AppendLine($"**{results.Count(r => r.Pass)}/{tests.Count} passed**");
        sb.AppendLine();
        sb.AppendLine($"---\n*Generated at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC*");
        return sb.ToString();
    }

    private static string Truncate(string? text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLen ? text : text[..maxLen] + "...";
    }

    public static async Task RunBatchAsync(string layer = "all")
    {
        var testFile = FindTestFile();
        if (testFile == null)
        {
            Console.WriteLine("ERROR: docs/testprompts.txt not found");
            return;
        }

        var tests = ParseTestPrompts(testFile, layer);
        Console.WriteLine($"=== LTAI Test Suite: {tests.Count} tests (layer={layer}) ===\n");

        var pass = 0; var fail = 0;
        foreach (var (id, layerName, query) in tests)
        {
            Console.Write($"[{layerName}] [{id}] ");
            try
            {
                await RunAsync(query, 1, null, null, false).ConfigureAwait(false);
                Console.WriteLine("OK");
                pass++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL: {ex.Message}");
                fail++;
            }
        }

        Console.WriteLine($"\n=== Results: {pass} PASS, {fail} FAIL ===");
    }

    private static string? FindTestFile()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "docs", "testprompts.txt"),
            Path.Combine(Directory.GetCurrentDirectory(), "docs", "testprompts.txt"),
            Path.Combine(FindRootDirectory(AppContext.BaseDirectory, "docs") ?? "", "docs", "testprompts.txt")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static List<(string Id, string Layer, string Query)> ParseTestPrompts(string path, string filterLayer)
    {
        var results = new List<(string, string, string)>();
        var currentLayer = "";
        var currentId = "";

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("# 编号") || trimmed.StartsWith("# 使用") || trimmed.StartsWith("# 验证"))
                continue;

            if (trimmed.StartsWith("## L")) { currentLayer = trimmed[3..].Split(' ')[0]; continue; }
            if (trimmed.StartsWith("## 跨层")) { currentLayer = "CHAOS"; continue; }

            if (trimmed.StartsWith("# ") && trimmed.Contains("-") && trimmed.Length > 5)
            {
                var parts = trimmed[2..].Split(' ', 2);
                if (parts.Length >= 1 && parts[0].Contains("-"))
                    currentId = parts[0];
                continue;
            }

            if (!string.IsNullOrEmpty(currentId) && !trimmed.StartsWith("#") && !trimmed.StartsWith("##"))
            {
                if (filterLayer == "all" || filterLayer == currentLayer)
                    results.Add((currentId, currentLayer, trimmed));
                currentId = "";
            }
        }

        return results;
    }

    /// <summary>
    /// Auto-generate a minimal appsettings.json from environment variables.
    /// Avoids System.Text.Json reflection serialization to work around
    /// JsonSerializerIsReflectionEnabledByDefault=false in the project.
    /// </summary>
    private static void AutoBootstrapConfig(string configPath)
    {
        var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
                  ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                  ?? "";

        var dir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Build JSON manually to avoid reflection-based serialization
        var json = $$"""
{
  "ltai": {
    "ai": {
      "providers": {
        "deepseek": {
          "endpoint": "https://api.deepseek.com",
          "model": "deepseek-v4-pro",
          "apiKey": "{{apiKey}}"
        },
        "deepseek-fast": {
          "endpoint": "https://api.deepseek.com",
          "model": "deepseek-v4-flash",
          "apiKey": "{{apiKey}}"
        }
      },
      "l1": { "provider": "deepseek-fast", "model": "deepseek-chat" },
      "l2": { "provider": "deepseek", "model": "deepseek-reasoner" },
      "l0": { "provider": "local", "model": "embed" },
      "maxTokens": 8192
    }
  }
}
""";
        File.WriteAllText(configPath, json);
    }

    private static string? FindRootDirectory(string startDir, string markerDir)
    {
        var current = startDir;
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current, markerDir)))
                return current;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }
}
