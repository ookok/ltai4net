using System.Reflection;
using System.Text.Json;
using Spectre.Console;
using LTAI.Core;
using LTAI.Agent.Vector;

namespace LTAI.Cli;

partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.Title = "LTAI CLI";
        AnsiConsole.Write(new FigletText("LTAI CLI").Color(Color.Green));
        AnsiConsole.MarkupLine("[grey]LivingTree AI — Agent Framework[/] [blue]⚡[/]");

        if (args.Length == 0)
        {
            ShowHelp();
            AnsiConsole.MarkupLine("\n[grey]Press any key to exit...[/]");
            System.Console.ReadKey(true);
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        return command switch
        {
            "env" => HandleEnv(args[1..]),
            "migrate" => await HandleMigrate(args[1..]).ConfigureAwait(false),
            "textpad" => HandleTextPad(args[1..]),
            "dashboard" or "dash" => HandleDashboard(),
            "health" or "--health" or "hc" => await HandleHealth().ConfigureAwait(false),
            "version" or "--version" or "-v" => ShowVersion(),
            _ => ShowHelp()
        };
    }

    private static int ShowHelp()
    {
        var table = new Table();
        table.AddColumn("Command");
        table.AddColumn("Description");
        table.AddRow("env", "Show / export / import environment variables");
        table.AddRow("env get <name>", "Get a single environment variable");
        table.AddRow("env set <name> <value>", "Set an environment variable");
        table.AddRow("env export <path>", "Export env vars to JSON file");
        table.AddRow("env import <path>", "Import env vars from JSON file");
        table.AddRow("migrate", "迁移 LiteDB → SQLite 知识图谱");
        table.AddRow("textpad [path]", "文件浏览器/编辑器");
        table.AddRow("dashboard or dash", "实时仪表盘");
        table.AddRow("health", "系统健康检查 — 组件/磁盘/缓存诊断");
        table.AddRow("version", "Show version");
        AnsiConsole.Write(table);
        return 0;
    }

    private static int HandleEnv(string[] subArgs)
    {
        if (subArgs.Length == 0)
            return ShowEnv();

        return subArgs[0].ToLowerInvariant() switch
        {
            "get" => GetEnv(subArgs.Length > 1 ? subArgs[1] : null),
            "set" => SetEnv(subArgs),
            "export" => ExportEnv(subArgs.Length > 1 ? subArgs[1] : null),
            "import" => ImportEnv(subArgs.Length > 1 ? subArgs[1] : null),
            _ => ShowEnv()
        };
    }

    // ═══════════════════════════════════════════
    //  env (list)
    // ═══════════════════════════════════════════

    private static int ShowEnv()
    {
        var knownVars = new[]
        {
            ("DEEPSEEK_API_KEY", "DeepSeek"),
            ("OPENAI_API_KEY",   "OpenAI"),
            ("SILICONFLOW_API_KEY", "SiliconFlow"),
            ("DASHSCOPE_API_KEY", "Aliyun"),
            ("ZHIPU_API_KEY",   "Zhipu"),
            ("BRAVE_API_KEY",   "Brave Search"),
            ("SERPER_API_KEY",  "Serper (Google)"),
            ("UNSPLASH_KEY",    "Unsplash"),
            ("WEATHER_KEY",     "Weather"),
            ("AMAP_KEY",        "Amap (GIS)"),
            ("BAIDU_MAP_KEY",   "Baidu Map"),
        };

        AnsiConsole.MarkupLine("[bold]Configured API Keys[/]");
        var table = new Table();
        table.AddColumn("Provider");
        table.AddColumn("Status");
        table.AddColumn("Key (preview)");

        foreach (var (envVar, label) in knownVars)
        {
            var val = Environment.GetEnvironmentVariable(envVar);
            var status = !string.IsNullOrEmpty(val) ? "[green]✓[/]" : "[red]✗[/]";
            var preview = val != null ? val[..Math.Min(8, val.Length)] + "..." : "not set";
            table.AddRow(label, status, preview);
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("\n[grey]Tip: use 'env export <file>' to backup, 'env import <file>' to restore[/]");
        return 0;
    }

    // ═══════════════════════════════════════════
    //  env get <name>
    // ═══════════════════════════════════════════

    private static int GetEnv(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            AnsiConsole.MarkupLine("[red]Usage: env get <variable-name>[/]");
            return 1;
        }

        var val = Environment.GetEnvironmentVariable(name);
        if (val == null)
        {
            AnsiConsole.MarkupLine($"[yellow]'{name}' is not set[/]");
            return 1;
        }

        // Redact secrets in display
        var display = name.Contains("KEY") || name.Contains("SECRET") || name.Contains("PASSWORD") || name.Contains("TOKEN")
            ? val.Length > 8 ? val[..8] + "..." : "***"
            : val;

        AnsiConsole.MarkupLine($"[bold]{name}[/] = [green]{display}[/]");
        return 0;
    }

    // ═══════════════════════════════════════════
    //  env set <name> <value>
    // ═══════════════════════════════════════════

    private static int SetEnv(string[] args)
    {
        if (args.Length < 3)
        {
            AnsiConsole.MarkupLine("[red]Usage: env set <variable-name> <value>[/]");
            AnsiConsole.MarkupLine("[grey]  Values with spaces must be quoted[/]");
            return 1;
        }

        var name = args[1];
        var value = string.Join(" ", args[2..]); // Support spaces in value

        // Preview for display (redact secrets)
        var preview = name.Contains("KEY") || name.Contains("SECRET") || name.Contains("PASSWORD") || name.Contains("TOKEN")
            ? value.Length > 8 ? value[..8] + "..." : "***"
            : value;

        AnsiConsole.MarkupLine($"[yellow]⚠️  About to set environment variable:[/]");
        AnsiConsole.MarkupLine($"  [bold]{name}[/] = [green]{preview}[/]");
        AnsiConsole.MarkupLine("[grey]  This change only affects the current process and its children.[/]");
        AnsiConsole.MarkupLine("[grey]  It will be lost when the process exits.[/]");

        if (!AnsiConsole.Confirm("Continue?"))
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            return 1;
        }

        LTAI.Core.Configuration.SecretManager.Set(name, value);
        AnsiConsole.MarkupLine($"[green]✅ {name}[/] = [green]{preview}[/]");
        return 0;
    }

    // ═══════════════════════════════════════════
    //  env export <file>
    // ═══════════════════════════════════════════

    private static int ExportEnv(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            AnsiConsole.MarkupLine("[red]Usage: env export <file-path>[/]");
            AnsiConsole.MarkupLine("[grey]  e.g. env export C:\\Users\\User\\Desktop\\secrets_export.json[/]");
            return 1;
        }

        // Prompt for which variables to export
        var allVars = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Select(e => new { Key = e.Key?.ToString() ?? "", Value = e.Value?.ToString() ?? "" })
            .Where(e => !string.IsNullOrEmpty(e.Key))
            .OrderBy(e => e.Key)
            .ToList();

        // Show available keys with potential secrets marked
        var secretKeys = allVars
            .Where(e => e.Key.Contains("KEY") || e.Key.Contains("SECRET") || e.Key.Contains("PASSWORD") || e.Key.Contains("TOKEN") || e.Key.Contains("API"))
            .Select(e => e.Key)
            .ToList();

        AnsiConsole.MarkupLine("[bold]Select variables to export:[/]");

        var selected = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("Choose environment variables to export")
                .PageSize(15)
                .MoreChoicesText("[grey](scroll down for more)[/]")
                .InstructionsText("[grey](space to select, enter to confirm)[/]")
                .AddChoiceGroup("🔑 API Keys (auto-selected)", secretKeys.Select(k => $"{k}"))
                .AddChoices(allVars
                    .Where(e => !secretKeys.Contains(e.Key))
                    .Select(e => e.Key)));

        if (selected.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No variables selected. Export cancelled.[/]");
            return 1;
        }

        // Build export dict
        var export = new Dictionary<string, string>();
        foreach (var key in selected)
        {
            var val = Environment.GetEnvironmentVariable(key);
            if (val != null)
                export[key] = val;
        }

        // Write JSON file
        try
        {
            var json = JsonSerializer.Serialize(export, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, json);

            AnsiConsole.MarkupLine($"[green]✅ Exported {export.Count} variables to {filePath}[/]");
            AnsiConsole.MarkupLine("[yellow]⚠️  This file contains sensitive API keys. Keep it secure![/]");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Export failed: {ex.Message}[/]");
            return 1;
        }
    }

    // ═══════════════════════════════════════════
    //  env import <file>
    // ═══════════════════════════════════════════

    private static int ImportEnv(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            AnsiConsole.MarkupLine("[red]Usage: env import <file-path>[/]");
            AnsiConsole.MarkupLine("[grey]  e.g. env import C:\\Users\\User\\Desktop\\secrets_export.json[/]");
            return 1;
        }

        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]File not found: {filePath}[/]");
            return 1;
        }

        // Read and parse JSON
        Dictionary<string, string>? import;
        try
        {
            var json = File.ReadAllText(filePath);
            import = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException ex)
        {
            AnsiConsole.MarkupLine($"[red]Invalid JSON: {ex.Message}[/]");
            return 1;
        }

        if (import == null || import.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No variables found in file.[/]");
            return 1;
        }

        // Show what will be imported
        AnsiConsole.MarkupLine("[bold]Variables to import:[/]");
        var table = new Table();
        table.AddColumn("Variable");
        table.AddColumn("Value (preview)");
        table.AddColumn("Action");

        var existingCount = 0;
        foreach (var (key, val) in import.OrderBy(kv => kv.Key))
        {
            var existing = Environment.GetEnvironmentVariable(key);
            var action = existing != null ? "[yellow]overwrite[/]" : "[green]set[/]";
            if (existing != null) existingCount++;

            var preview = key.Contains("KEY") || key.Contains("SECRET") || key.Contains("PASSWORD") || key.Contains("TOKEN")
                ? val.Length > 8 ? val[..8] + "..." : "***"
                : val;

            table.AddRow(key, preview, action);
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]  {import.Count} variables ({existingCount} will overwrite existing values)[/]");

        // Confirm
        if (!AnsiConsole.Confirm("\nImport these variables?"))
        {
            AnsiConsole.MarkupLine("[yellow]Import cancelled.[/]");
            return 1;
        }

        // Apply
        var setCount = 0;
        foreach (var (key, val) in import)
        {
            LTAI.Core.Configuration.SecretManager.Set(key, val);
            setCount++;
        }

        AnsiConsole.MarkupLine($"[green]✅ Imported {setCount} variables from {filePath}[/]");
        AnsiConsole.MarkupLine("[grey]Note: Changes only affect the current process. Restart required for other tools.[/]");
        return 0;
    }

    private static int ShowVersion()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        AnsiConsole.MarkupLine($"[bold]LTAI CLI[/] v{ver}");
        AnsiConsole.MarkupLine("[grey]Agent Framework: Microsoft.Agents.AI (git submodule extern/agent-framework)[/]");
        return 0;
    }

    // ═══════════════════════════════════════════
    //  health — 系统健康检查
    // ═══════════════════════════════════════════

    private static async Task<int> HandleHealth()
    {
        AnsiConsole.MarkupLine("[bold]🔍 LTAI 系统健康检查[/]\n");

        var allPass = true;

        // 1. KgStore 可访问性
        try
        {
            var dbPath = Path.Combine(Directory.GetCurrentDirectory(), ".livingtree", "kg.db");
            if (File.Exists(dbPath))
            {
                using var store = new KgStore(dbPath);
                var nodeCount = await store.NodeCount().ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[green]✅ KgStore[/] — 节点: [bold]{nodeCount}[/] — {new FileInfo(dbPath).Length / 1024}KB");
                store.Dispose();
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]⚠️  KgStore[/] — 数据库尚未创建 (首次运行会自动创建)");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]❌ KgStore[/] — {ex.Message.EscapeMarkup()}");
            allPass = false;
        }

        // 2. LLM 提供商
        try
        {
            var keys = new[] { ("DeepSeek","DEEPSEEK_API_KEY"), ("OpenAI","OPENAI_API_KEY"),
                ("SiliconFlow","SILICONFLOW_API_KEY"), ("Brave","BRAVE_API_KEY") };
            foreach (var (name, env) in keys)
            {
                var hasKey = !string.IsNullOrEmpty(LTAI.Core.Configuration.SecretManager.Get(env));
                AnsiConsole.MarkupLine(hasKey
                    ? $"[green]  ✅ {name}[/] — API Key 已配置"
                    : $"[grey]  —   {name}[/] — 未设置 (可选)");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]  ❌ LLM 提供商检查失败: {ex.Message.EscapeMarkup()}[/]");
            allPass = false;
        }

        // 3. 磁盘空间
        try
        {
            var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady);
            if (drive != null)
            {
                var freeGb = drive.AvailableFreeSpace / 1.0 / (1024 * 1024 * 1024);
                var status = freeGb > 1 ? "[green]" : "[yellow]";
                AnsiConsole.MarkupLine($"{status}  💾 磁盘[/] — {drive.Name} 剩余 {freeGb:F1}GB");
            }
        }
        catch { /* skip disk check on restricted systems */ }

        // 4. 缓存 / 运行时状态
        AnsiConsole.MarkupLine($"[grey]  📊 缓存命中[/] — {LTAI.Core.Configuration.UsageTracker.CacheHitRate:F1}% ({LTAI.Core.Configuration.UsageTracker.CacheHits}/{LTAI.Core.Configuration.UsageTracker.CacheMisses + LTAI.Core.Configuration.UsageTracker.CacheHits})");
        AnsiConsole.MarkupLine($"[grey]  💰 费用[/] — {LTAI.Core.Configuration.UsageTracker.CostDisplay} | {LTAI.Core.Configuration.UsageTracker.TotalTokens:N0} tokens[/]");
        AnsiConsole.MarkupLine($"[grey]  🕐 运行时间[/] — {LTAI.Core.Configuration.UsageTracker.Uptime:hh\\:mm\\:ss}");

        // 5. 网络可达性
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var pingTask = http.GetAsync("https://api.deepseek.com/v1/models");
            if (pingTask.Wait(TimeSpan.FromSeconds(3)))
            {
                using var resp = pingTask.Result;
                AnsiConsole.MarkupLine(resp.IsSuccessStatusCode
                    ? $"[green]  ✅ 网络[/] — DeepSeek API 可达 ({resp.StatusCode})"
                    : $"[yellow]  ⚠️  网络[/] — DeepSeek 返回 {(int)resp.StatusCode}");
            }
            else
                AnsiConsole.MarkupLine("[yellow]  ⚠️  网络[/] — DeepSeek API 超时 (3s)");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]  ⚠️  网络[/] — {ex.Message.EscapeMarkup()}");
        }

        AnsiConsole.MarkupLine(allPass ? "\n[bold green]✅ 所有检查通过[/]" : "\n[bold yellow]⚠️  部分检查未通过，请查看上方详情[/]");
        return allPass ? 0 : 1;
    }

    // ═══════════════════════════════════════════
    //  migrate — LiteDB → SQLite 知识图谱迁移
    // ═══════════════════════════════════════════

    private static async Task<int> HandleMigrate(string[] args)
    {
        AnsiConsole.MarkupLine("[bold]知识图谱迁移[/]");
        var ws = Directory.GetCurrentDirectory();
        var oldDb = Path.Combine(ws, ".livingtree", "graph.db");
        var newDb = Path.Combine(ws, ".livingtree", "kg.db");

        // Step 1: 检查是否有旧 LiteDB 数据库
        if (File.Exists(oldDb))
        {
            AnsiConsole.MarkupLine($"[yellow]发现旧 LiteDB 数据库: {oldDb}[/]");
            AnsiConsole.MarkupLine("[grey]LiteDB 已被 SQLite 替代。旧数据无法自动迁移（LiteDB 依赖已移除）。[/]");
            AnsiConsole.MarkupLine("[grey]你可以手动重命名或删除旧文件以释放磁盘空间。[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[green]✓ 无旧 LiteDB 数据库[/]");
        }

        // Step 2: 检查新 SQLite 数据库
        var store = new LTAI.Agent.Vector.KgStore(newDb);
        var stats = await store.Stats().ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]✓ SQLite 知识图谱: {newDb}[/]");
        AnsiConsole.MarkupLine(stats);
        store.Dispose();
        return 0;
    }

    // ═══════════════════════════════════════════
    //  textpad — 文件浏览器/编辑器
    // ═══════════════════════════════════════════

    private static int HandleTextPad(string[] args)
    {
        var path = args.Length > 0 ? args[0] : ".";
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root) && !File.Exists(root))
        {
            AnsiConsole.MarkupLine($"[red]路径不存在: {root}[/]");
            return 1;
        }
        if (File.Exists(root))
        {
            // 直接查看文件
            var content = File.ReadAllText(root);
            var panel = new Panel(content.EscapeMarkup())
                .Header($"[bold]{Path.GetFileName(root)}[/]").BorderColor(Color.Green).Expand();
            AnsiConsole.Write(panel);
            return 0;
        }
        // 目录浏览器
        var running = true;
        var currentDir = root;
        while (running)
        {
            Console.Clear();
            AnsiConsole.MarkupLine($"[bold]文件浏览器[/] — [grey]{currentDir}[/]");
            var items = new List<string>();
            try
            {
                items.AddRange(Directory.GetDirectories(currentDir)
                    .Select(d => $"[cyan]📁 {Path.GetFileName(d)}/[/]"));
                items.AddRange(Directory.GetFiles(currentDir)
                    .Select(f => $"[grey]📄 {Path.GetFileName(f)}[/]"));
            }
            catch { AnsiConsole.MarkupLine("[red]无法读取目录[/]"); break; }
            if (items.Count == 0) { AnsiConsole.MarkupLine("[grey](空目录)[/]"); break; }
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>().Title("[yellow]选择文件/目录:[/]").PageSize(20).AddChoices(items));
            if (string.IsNullOrEmpty(choice)) break;
            var fp = Path.GetFullPath(Path.Combine(currentDir, choice.Replace("📁 ", "").Replace("📄 ", "")));
            // Strip markup tags
            var clean = fp;
            if (Directory.Exists(clean)) { currentDir = clean; continue; }
            if (!File.Exists(clean)) break;
            var ext = Path.GetExtension(clean).ToLowerInvariant();
            if (ext is ".md" or ".txt")
            {
                // 渲染 Markdown / 文本
                var text = File.ReadAllText(clean);
                AnsiConsole.Write(new Panel(text.EscapeMarkup())
                    .Header($"[bold]{Path.GetFileName(clean)}[/]").BorderColor(Color.Blue).Expand());
            }
            else
            {
                var lines = File.ReadAllLines(clean);
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < Math.Min(lines.Length, 200); i++)
                    sb.AppendLine($"[grey]{i + 1,4}[/] {lines[i].EscapeMarkup()}");
                if (lines.Length > 200)
                    sb.AppendLine($"[grey]... 仅显示前 200 行，共 {lines.Length} 行[/]");
                AnsiConsole.Write(new Panel(sb.ToString().TrimEnd())
                    .Header($"[bold]{Path.GetFileName(clean)}[/]").BorderColor(Color.Green).Expand());
            }
            AnsiConsole.MarkupLine("[grey]按任意键继续...[/]");
            System.Console.ReadKey(true);
        }
        return 0;
    }

    // ═══════════════════════════════════════════
    //  dashboard — 实时仪表盘
    // ═══════════════════════════════════════════

    private static int HandleDashboard()
    {
        var table = new Table();
        table.AddColumn("指标"); table.AddColumn("值");
        table.AddRow("当前模型", LTAI.Core.Configuration.UsageTracker.ActiveModel);
        table.AddRow("输入 Token", LTAI.Core.Configuration.UsageTracker.PromptTokens.ToString("N0"));
        table.AddRow("输出 Token", LTAI.Core.Configuration.UsageTracker.CompletionTokens.ToString("N0"));
        table.AddRow("请求次数", LTAI.Core.Configuration.UsageTracker.Requests.ToString("N0"));
        table.AddRow("缓存命中", $"{LTAI.Core.Configuration.UsageTracker.CacheHitRate:F1}%");
        table.AddRow("预估费用", LTAI.Core.Configuration.UsageTracker.CostDisplay);
        table.AddRow("余额", LTAI.Core.Configuration.UsageTracker.BalanceDisplay);
        AnsiConsole.Write(table);

        var pct = LTAI.Core.Configuration.UsageTracker.ContextRatio();
        var ctxChart = new BarChart().Width(50).HideValues()
            .AddItem("上下文", pct * 100, Color.Yellow)
            .AddItem("剩余", (1 - pct) * 100, Color.Grey35);
        AnsiConsole.Write(ctxChart);
        return 0;
    }
}
