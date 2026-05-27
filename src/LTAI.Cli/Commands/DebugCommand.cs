using System.ComponentModel;
using Spectre.Console.Cli;

namespace LTAI.Cli.Commands;

public sealed class DebugCommand : AsyncCommand<DebugCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--query")]
        [Description("Specific query to trace")]
        public string? Query { get; init; }

        [CommandOption("--count")]
        [Description("Number of test cases")]
        [DefaultValue(20)]
        public int Count { get; init; } = 20;

        [CommandOption("--difficulty")]
        [Description("Filter by difficulty (Simple, Moderate, Complex, OOD)")]
        public string? Difficulty { get; init; }

        [CommandOption("--domain")]
        [Description("Filter by domain")]
        public string? Domain { get; init; }

        [CommandOption("--report")]
        [Description("Generate a report")]
        [DefaultValue(false)]
        public bool GenerateReport { get; init; }

        [CommandOption("--batch")]
        [Description("Run tests from testprompts.txt file")]
        [DefaultValue(false)]
        public bool Batch { get; init; }

        [CommandOption("--layer")]
        [Description("Filter batch by layer (L0-L5, CHAOS, all)")]
        public string? Layer { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (settings.Batch)
        {
            await DebugMode.RunBatchAsync(settings.Layer ?? "all").ConfigureAwait(false);
            return 0;
        }

        await DebugMode.RunAsync(
            settings.Query, settings.Count, settings.Difficulty, settings.Domain, settings.GenerateReport).ConfigureAwait(false);
        return 0;
    }
}
