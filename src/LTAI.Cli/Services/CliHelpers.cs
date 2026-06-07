using Spectre.Console;
using LTAI.Core.Configuration;

namespace LTAI.Cli;

partial class Program
{
    /// <summary>Shared console helpers for CLI commands.</summary>
    internal static class CliHelpers
    {
        public static string RedactSecret(string name, string value) =>
            name.Contains("KEY") || name.Contains("SECRET") || name.Contains("PASSWORD") || name.Contains("TOKEN")
                ? (value.Length > 8 ? value[..8] + "..." : "***")
                : value;

        public static string Escape(string text) => text.EscapeMarkup();

        public static void WriteTable(string title, string[] columns, IEnumerable<string[]> rows)
        {
            var table = new Table().Border(TableBorder.Rounded);
            foreach (var col in columns) table.AddColumn(col);
            foreach (var row in rows) table.AddRow(row);
            AnsiConsole.Write(table);
        }

        public static void UsageTrackerBar()
        {
            AnsiConsole.MarkupLine($"[grey]  📊 缓存命中[/] — {UsageTracker.CacheHitRate:F1}% ({UsageTracker.CacheHits}/{UsageTracker.CacheMisses + UsageTracker.CacheHits})");
            AnsiConsole.MarkupLine($"[grey]  💰 费用[/] — {UsageTracker.CostDisplay} | {UsageTracker.TotalTokens:N0} tokens[/]");
            AnsiConsole.MarkupLine($"[grey]  🕐 运行时间[/] — {UsageTracker.Uptime:hh\\:mm\\:ss}");
        }
    }
}
