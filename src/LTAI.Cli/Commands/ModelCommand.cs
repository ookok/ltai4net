using Spectre.Console;
using LTAI.AI;

namespace LTAI.Cli;

partial class Program
{
    internal static int HandleModel(string[] subArgs)
    {
        if (subArgs.Length == 0) { ShowModelHelp(); return 0; }
        return subArgs[0].ToLowerInvariant() switch
        {
            "list" or "ls" => ListModels(),
            "switch" or "use" => SwitchModel(subArgs.ElementAtOrDefault(1)),
            _ => ShowModelHelp()
        };
    }

    private static int ShowModelHelp()
    {
        AnsiConsole.MarkupLine("[bold]Usage:[/]");
        AnsiConsole.MarkupLine("  [green]ltai model list[/]        — List available ONNX embedding models");
        AnsiConsole.MarkupLine("  [green]ltai model switch <name>[/] — Switch active embedding model");
        return 0;
    }

    private static int ListModels()
    {
        var models = LocalEmbedder.ListAvailableModels();
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Embedding Models[/]");
        table.AddColumn("ID"); table.AddColumn("Dimensions"); table.AddColumn("Downloaded");

        foreach (var m in models)
        {
            var downloaded = m.Downloaded || m.QuantizedDownloaded ? "[green]✓[/]" : "[grey]—[/]";
            table.AddRow(m.Id.EscapeMarkup(), m.Dimension.ToString(), downloaded);
        }
        AnsiConsole.Write(table);

        var llmTable = new Table().Border(TableBorder.Rounded).Title("[bold]LLM Providers (config)[/]");
        llmTable.AddColumn("Slot"); llmTable.AddColumn("Model");
        foreach (var kvp in new[] { ("L1 (Flash)", "config"),
            ("L2 (Pro)", "config") })
            llmTable.AddRow(kvp.Item1.EscapeMarkup(), kvp.Item2.EscapeMarkup());
        AnsiConsole.Write(llmTable);
        return 0;
    }

    private static int SwitchModel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) { Error("Usage: ltai model switch <name>"); return 1; }
        try
        {
            var embedder = new LocalEmbedder();
            var ok = embedder.SwitchModel(name);
            AnsiConsole.MarkupLine(ok ? $"[green]✅ Switched to '{name.EscapeMarkup()}'[/]" : $"[red]Failed to switch to '{name.EscapeMarkup()}'[/]");
            return ok ? 0 : 1;
        }
        catch (Exception ex) { Error($"Switch failed: {ex.Message}"); return 1; }
    }
}
