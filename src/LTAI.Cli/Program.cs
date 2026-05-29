using System.Reflection;
using System.Text.Json;
using Spectre.Console;
using LTAI.Core;

namespace LTAI.Cli;

partial class Program
{
    public static int Main(string[] args)
    {
        AnsiConsole.Write(new FigletText("LTAI CLI").Color(Color.Green));
        AnsiConsole.MarkupLine("[grey]LivingTree AI — MS Agent Framework 1.8.0[/]");

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

        Environment.SetEnvironmentVariable(name, value);
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
            Environment.SetEnvironmentVariable(key, val);
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
        AnsiConsole.MarkupLine("[grey]Agent Framework: Microsoft.Agents.AI 1.8.0[/]");
        return 0;
    }
}
