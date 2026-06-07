using Spectre.Console;
using LTAI.Agent.Vector;

namespace LTAI.Cli;

partial class Program
{
    internal static async Task<int> HandleMigrate(string[] args)
    {
        AnsiConsole.MarkupLine("[bold]知识图谱迁移[/]");
        var ws = Directory.GetCurrentDirectory();
        var oldDb = Path.Combine(ws, ".livingtree", "graph.db");
        var newDb = Path.Combine(ws, ".livingtree", "kg.db");

        if (File.Exists(oldDb))
        {
            AnsiConsole.MarkupLine($"[yellow]旧 LiteDB 数据库: {oldDb}[/]");
            AnsiConsole.MarkupLine("[grey]LiteDB 已移除。旧数据无法自动迁移。[/]");
        }
        else
            AnsiConsole.MarkupLine("[green]✓ 无旧 LiteDB 数据库[/]");

        var store = new KgStore(newDb);
        var stats = await store.Stats().ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]✓ SQLite 知识图谱: {newDb}[/]");
        AnsiConsole.MarkupLine(stats);
        store.Dispose();
        return 0;
    }
}
