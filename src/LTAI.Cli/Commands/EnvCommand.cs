using System.Text.Json;
using Spectre.Console;
using LTAI.Core.Configuration;

namespace LTAI.Cli;

partial class Program
{
    internal static int HandleEnv(string[] subArgs)
    {
        if (subArgs.Length == 0) return ShowEnv();
        return subArgs[0].ToLowerInvariant() switch
        {
            "get" => GetEnv(subArgs.Length > 1 ? subArgs[1] : null),
            "set" => SetEnv(subArgs),
            "export" => ExportEnv(subArgs.Length > 1 ? subArgs[1] : null),
            "import" => ImportEnv(subArgs.Length > 1 ? subArgs[1] : null),
            _ => ShowEnv()
        };
    }

    private static int ShowEnv()
    {
        var knownVars = new[]
        {
            ("DEEPSEEK_API_KEY", "DeepSeek"), ("OPENAI_API_KEY", "OpenAI"),
            ("SILICONFLOW_API_KEY", "SiliconFlow"), ("DASHSCOPE_API_KEY", "Aliyun"),
            ("ZHIPU_API_KEY", "Zhipu"), ("BRAVE_API_KEY", "Brave Search"),
            ("SERPER_API_KEY", "Serper (Google)"), ("UNSPLASH_KEY", "Unsplash"),
            ("WEATHER_KEY", "Weather"), ("AMAP_KEY", "Amap (GIS)"),
            ("BAIDU_MAP_KEY", "Baidu Map"),
        };

        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Configured API Keys[/]");
        table.AddColumn("Provider"); table.AddColumn("Status"); table.AddColumn("Key (preview)");
        foreach (var (envVar, label) in knownVars)
        {
            var val = SecretManager.Get(envVar);
            var status = !string.IsNullOrEmpty(val) ? "[green]✓[/]" : "[red]✗[/]";
            table.AddRow(label, status, val != null ? val[..Math.Min(8, val.Length)] + "..." : "not set");
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("\n[grey]Tip: 'env export <file>' to backup, 'env import <file>' to restore[/]");
        return 0;
    }

    private static int GetEnv(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) { Error("Usage: env get <variable-name>"); return 1; }
        var val = SecretManager.Get(name);
        if (val == null) { AnsiConsole.MarkupLine($"[yellow]'{name.EscapeMarkup()}' is not set[/]"); return 1; }
        var display = CliHelpers.RedactSecret(name, val);
        AnsiConsole.MarkupLine($"[bold]{name.EscapeMarkup()}[/] = [green]{display.EscapeMarkup()}[/]");
        return 0;
    }

    private static int SetEnv(string[] args)
    {
        if (args.Length < 3) { Error("Usage: env set <variable-name> <value>"); return 1; }
        var name = args[1];
        var value = string.Join(" ", args[2..]);
        var preview = CliHelpers.RedactSecret(name, value);

        AnsiConsole.MarkupLine($"[yellow]⚠️  Set env var:[/] [bold]{name.EscapeMarkup()}[/] = [green]{preview.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine("[grey]  This session only — lost on process exit.[/]");

        if (!AnsiConsole.Confirm("Continue?")) { AnsiConsole.MarkupLine("[yellow]Cancelled.[/]"); return 1; }

        SecretManager.Set(name, value);
        AnsiConsole.MarkupLine($"[green]✅ {name.EscapeMarkup()}[/] set");
        return 0;
    }

    private static int ExportEnv(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) { Error("Usage: env export <file-path>"); return 1; }
        var allVars = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Select(e => new { Key = e.Key?.ToString() ?? "", Value = e.Value?.ToString() ?? "" })
            .Where(e => !string.IsNullOrEmpty(e.Key)).OrderBy(e => e.Key).ToList();

        var secretKeys = allVars.Where(e => e.Key.Contains("KEY") || e.Key.Contains("SECRET")
            || e.Key.Contains("PASSWORD") || e.Key.Contains("TOKEN") || e.Key.Contains("API"))
            .Select(e => e.Key).ToList();

        var selected = AnsiConsole.Prompt(new MultiSelectionPrompt<string>()
            .Title("Choose environment variables to export").PageSize(15)
            .MoreChoicesText("[grey](scroll)[/]").InstructionsText("[grey](space to select, enter to confirm)[/]")
            .AddChoiceGroup("🔑 API Keys", secretKeys)
            .AddChoices(allVars.Where(e => !secretKeys.Contains(e.Key)).Select(e => e.Key)));

        if (selected.Count == 0) { AnsiConsole.MarkupLine("[yellow]No selection.[/]"); return 1; }

        var export = new Dictionary<string, string>();
        foreach (var key in selected)
        {
            var val = SecretManager.Get(key);
            if (val != null) export[key] = val;
        }

        try
        {
            var json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, json);
            AnsiConsole.MarkupLine($"[green]✅ Exported {export.Count} vars to {filePath.EscapeMarkup()}[/]");
            AnsiConsole.MarkupLine("[yellow]⚠️  Keep this file secure![/]");
            return 0;
        }
        catch (Exception ex) { Error($"Export failed: {ex.Message}"); return 1; }
    }

    private static int ImportEnv(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) { Error("Usage: env import <file-path>"); return 1; }
        if (!File.Exists(filePath)) { Error($"File not found: {filePath}"); return 1; }

        Dictionary<string, string>? import;
        try { import = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(filePath)); }
        catch (JsonException ex) { Error($"Invalid JSON: {ex.Message}"); return 1; }

        if (import == null || import.Count == 0) { AnsiConsole.MarkupLine("[yellow]No vars found.[/]"); return 1; }

        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Variables to import[/]");
        table.AddColumn("Var"); table.AddColumn("Preview"); table.AddColumn("Action");
        int existingCount = 0;
        foreach (var (key, val) in import.OrderBy(kv => kv.Key))
        {
            var existing = SecretManager.Get(key);
            if (existing != null) existingCount++;
            table.AddRow(key.EscapeMarkup(), CliHelpers.RedactSecret(key, val).EscapeMarkup(),
                existing != null ? "[yellow]overwrite[/]" : "[green]set[/]");
        }
        AnsiConsole.Write(table);
        if (!AnsiConsole.Confirm("\nImport?")) { AnsiConsole.MarkupLine("[yellow]Cancelled.[/]"); return 1; }

        foreach (var (key, val) in import) SecretManager.Set(key, val);
        AnsiConsole.MarkupLine($"[green]✅ Imported {import.Count} vars[/]");
        return 0;
    }
}