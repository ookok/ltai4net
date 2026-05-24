using System.ComponentModel;
using Spectre.Console.Cli;

namespace LTAI.Cli.Commands;

public sealed class ImproveCommand : AsyncCommand<ImproveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--auto")]
        [Description("Run full pipeline (scan + papers + propose)")]
        [DefaultValue(false)]
        public bool Auto { get; init; }

        [CommandOption("--scan")]
        [Description("Scan architecture for issues")]
        [DefaultValue(false)]
        public bool Scan { get; init; }

        [CommandOption("--papers")]
        [Description("Search recent AI papers")]
        [DefaultValue(false)]
        public bool Papers { get; init; }

        [CommandOption("--propose")]
        [Description("Generate reform proposals")]
        [DefaultValue(false)]
        public bool Propose { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        await ImproveMode.RunAsync(settings.Scan, settings.Papers, settings.Propose, settings.Auto);
        return 0;
    }
}
