using Spectre.Console;

namespace LTAI.Cli;

partial class Program
{
    // ════════════════════════════════════════════════════════════════
    // ltai env [get|set]
    // ════════════════════════════════════════════════════════════════

    private static async Task RunEnvAsync(string[] args)
    {
        var sub = args.FirstOrDefault()?.ToLowerInvariant();

        if (sub == "get")
        {
            await RunEnvGetAsync(args[1..]);
            return;
        }
        if (sub == "set")
        {
            await RunEnvSetAsync(args[1..]);
            return;
        }

        PrintAllEnvVars();
    }

    private static Task RunEnvGetAsync(string[] args)
    {
        var key = args.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key))
        {
            AnsiConsole.MarkupLine("[red]Usage: ltai env get <KEY>[/]");
            return Task.CompletedTask;
        }

        var value = ResolveEnvValue(key.ToUpperInvariant());
        if (value == null)
        {
            AnsiConsole.MarkupLine($"[yellow]Environment variable '{key}' is not set.[/]");
            return Task.CompletedTask;
        }

        var display = IsSecretKey(key) ? MaskSecret(value) : value;
        AnsiConsole.MarkupLine($"[bold]{key}[/] = {display}");
        return Task.CompletedTask;
    }

    private static async Task RunEnvSetAsync(string[] args)
    {
        if (args.Length < 2)
        {
            AnsiConsole.MarkupLine("[red]Usage: ltai env set <KEY> <VALUE>[/]");
            return;
        }

        var key = args[0].ToUpperInvariant();
        var value = args[1];
        var config = CliConfig.Load();

        switch (key)
        {
            case "LTAI_HOME":
                config.InstallPath = value;
                break;
            case "LTAI_WORKSPACE":
                config.WorkspaceRoot = value;
                break;
            case "LTAI_L1_API_KEY":
                config.L1ApiKey = value;
                break;
            case "LTAI_L2_API_KEY":
                config.L2ApiKey = value;
                break;
            default:
                Environment.SetEnvironmentVariable(key, value, EnvironmentVariableTarget.User);
                AnsiConsole.MarkupLine($"[green]{key}[/] set (session + user-level).");
                AnsiConsole.MarkupLine("[dim]Note: provider keys from appsettings.json take precedence. Restart CLI to reload.[/]");
                return;
        }

        config.Save();
        config.SetEnv();
        AnsiConsole.MarkupLine($"[green]{key}[/] saved to [dim]{CliConfig.ConfigPath}[/]");
    }

    private static void PrintAllEnvVars()
    {
        var config = CliConfig.Load();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Variable[/]").Width(30))
            .AddColumn(new TableColumn("[bold]Value[/]"))
            .AddColumn(new TableColumn("[bold]Source[/]").Width(12));

        AddEnvRow(table, "LTAI_HOME", config.InstallPath, "config.json", IsSecretKey("LTAI_HOME"));
        AddEnvRow(table, "LTAI_WORKSPACE", config.WorkspaceRoot, "config.json", IsSecretKey("LTAI_WORKSPACE"));
        AddEnvRow(table, "LTAI_L1_API_KEY", config.L1ApiKey, "config.json", IsSecretKey("LTAI_L1_API_KEY"));
        AddEnvRow(table, "LTAI_L2_API_KEY", config.L2ApiKey, "config.json", IsSecretKey("LTAI_L2_API_KEY"));

        table.AddEmptyRow();

        var providers = new Dictionary<string, string>
        {
            ["DEEPSEEK"] = "DEEPSEEK_API_KEY",
            ["OPENAI"] = "OPENAI_API_KEY",
            ["ANTHROPIC"] = "ANTHROPIC_API_KEY",
            ["GEMINI"] = "GEMINI_API_KEY",
            ["SILICONFLOW"] = "SILICONFLOW_API_KEY",
            ["DASHSCOPE"] = "DASHSCOPE_API_KEY",
            ["ZHIPU"] = "ZHIPU_API_KEY",
            ["HUNYUAN"] = "HUNYUAN_API_KEY",
            ["BAIDU"] = "BAIDU_API_KEY",
            ["SPARK"] = "SPARK_API_KEY",
            ["MOFANG"] = "MOFANG_API_KEY",
            ["NVIDIA"] = "NVIDIA_API_KEY",
            ["BAILING"] = "BAILING_API_KEY",
            ["STEPFUN"] = "STEPFUN_API_KEY",
            ["INTERNLM"] = "INTERNLM_API_KEY",
            ["SENSETIME"] = "SENSETIME_API_KEY",
            ["MODELSCOPE"] = "MODELSCOPE_API_KEY",
            ["OPENROUTER"] = "OPENROUTER_API_KEY",
            ["XIAOMI"] = "XIAOMI_API_KEY",
            ["LONGCAT"] = "LONGCAT_API_KEY",
            ["DMXAPI"] = "DMXAPI_API_KEY",
            ["VOLCENGINE"] = "VOLCENGINE_API_KEY",
            ["MOONSHOT"] = "MOONSHOT_API_KEY",
            ["MINIMAX"] = "MINIMAX_API_KEY",
            ["GROQ"] = "GROQ_API_KEY",
            ["KIRO"] = "KIRO_API_KEY",
            ["OPENCODE"] = "OPENCODE_API_KEY",
        };

        foreach (var (provider, envVar) in providers.OrderBy(p => p.Key))
        {
            var val = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(val))
                AddEnvRow(table, envVar, val, $"env ({provider})", IsSecretKey(envVar));
        }

        table.AddEmptyRow();
        table.AddRow(
            new Markup("[dim]Local providers (no key)[/]"),
            new Markup("[dim]Ollama / LMStudio / vLLM / LlamaCpp / OpenWebUI[/]"),
            new Markup("[dim]—[/]"));

        AnsiConsole.Write(table);
    }
}
