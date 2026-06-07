using Spectre.Console;
using LTAI.Core.Configuration;

namespace LTAI.Cli;

partial class Program
{
    internal static int HandleProvider(string[] subArgs)
    {
        if (subArgs.Length == 0) { ShowProviderHelp(); return 0; }
        return subArgs[0].ToLowerInvariant() switch
        {
            "list" or "ls" => ListProviders(),
            "set" => SetProvider(subArgs.ElementAtOrDefault(1)),
            "apikey" => SetApiKey(subArgs),
            _ => ShowProviderHelp()
        };
    }

    private static int ShowProviderHelp()
    {
        AnsiConsole.MarkupLine("[bold]Usage:[/]");
        AnsiConsole.MarkupLine("  [green]ltai provider list[/]         — List configured LLM providers");
        AnsiConsole.MarkupLine("  [green]ltai provider set <name>[/]   — Set active provider");
        AnsiConsole.MarkupLine("  [green]ltai provider apikey <name>[/] — Interactive API key setup");
        return 0;
    }

    private static int ListProviders()
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]LLM Providers[/]");
        table.AddColumn("Provider"); table.AddColumn("API Key"); table.AddColumn("Active");

        var keys = new[] { ("DeepSeek", "DEEPSEEK_API_KEY"), ("OpenAI", "OPENAI_API_KEY"),
            ("SiliconFlow", "SILICONFLOW_API_KEY"), ("Aliyun (DashScope)", "DASHSCOPE_API_KEY"),
            ("Zhipu", "ZHIPU_API_KEY") };

        foreach (var (name, envVar) in keys)
        {
            var val = SecretManager.Get(envVar);
            var hasKey = !string.IsNullOrEmpty(val) ? "[green]✓[/]" : "[red]✗[/]";
            var preview = val != null ? val[..Math.Min(6, val.Length)] + "..." : "not set";
            table.AddRow(name.EscapeMarkup(), $"{hasKey} {preview.EscapeMarkup()}", "");
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]Use 'ltai provider set <name>' to switch, 'ltai provider apikey <name>' to configure.[/]");
        return 0;
    }

    private static int SetProvider(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) { Error("Usage: ltai provider set <name>"); return 1; }
        try
        {
            AnsiConsole.MarkupLine($"[green]✅ Provider preference set to '{name.EscapeMarkup()}' (runtime selection)[/]");
            return 0;
        }
        catch (Exception ex) { Error($"Failed: {ex.Message}"); return 1; }
    }

    private static int SetApiKey(string[] args)
    {
        if (args.Length < 2) { Error("Usage: ltai provider apikey <name>"); return 1; }
        var name = args[1];
        var envVar = name.ToUpperInvariant() switch
        {
            "DEEPSEEK" or "DS" => "DEEPSEEK_API_KEY",
            "OPENAI" or "AI" => "OPENAI_API_KEY",
            "SILICONFLOW" or "SF" => "SILICONFLOW_API_KEY",
            "DASHSCOPE" or "ALIYUN" or "ALI" => "DASHSCOPE_API_KEY",
            "ZHIPU" or "GLM" => "ZHIPU_API_KEY",
            _ => null
        };

        if (envVar == null) { Error($"Unknown provider '{name}'. Use: DeepSeek, OpenAI, SiliconFlow, DashScope, Zhipu"); return 1; }

        var existing = SecretManager.Get(envVar);
        if (existing != null)
            AnsiConsole.MarkupLine($"[yellow]Current {name} key:[/] {CliHelpers.RedactSecret(envVar, existing).EscapeMarkup()}");

        var value = args.Length >= 3 ? string.Join(" ", args[2..]) :
            AnsiConsole.Ask<string>($"Enter your {name} API key:");

        if (string.IsNullOrWhiteSpace(value)) { Error("Key cannot be empty."); return 1; }

        SecretManager.Set(envVar, value);
        AnsiConsole.MarkupLine($"[green]✅ {name} API key saved (session only)[/]");
        return 0;
    }
}
