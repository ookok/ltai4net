using System.ComponentModel;
using Spectre.Console.Cli;

namespace LTAI.Cli.Commands;

public sealed class AutoFixCommand : AsyncCommand<AutoFixCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--target")]
        [Description("Path to project or file")]
        public string? Target { get; init; }

        [CommandOption("--args")]
        [Description("Arguments to pass to the target")]
        [DefaultValue("")]
        public string Args { get; init; } = "";

        [CommandOption("--attempts")]
        [Description("Maximum fix attempts")]
        [DefaultValue(3)]
        public int MaxAttempts { get; init; } = 3;

        [CommandOption("--analyze")]
        [Description("Analyze only, do not apply fixes")]
        [DefaultValue(false)]
        public bool Analyze { get; init; }

        [CommandOption("--scan")]
        [Description("Proactive static analysis — find bugs before running")]
        [DefaultValue(false)]
        public bool Scan { get; init; }

        [CommandOption("--gui")]
        [Description("GUI mode — process alive after timeout = success")]
        [DefaultValue(false)]
        public bool Gui { get; init; }

        [CommandOption("--timeout")]
        [Description("Process wait timeout in seconds (default 120, 10 for GUI)")]
        [DefaultValue(0)]
        public int TimeoutSecs { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var timeoutMs = settings.TimeoutSecs > 0 ? settings.TimeoutSecs * 1000 : (settings.Gui ? 10000 : 120000);
        await AutoFixMode.RunAsync(settings.Target, settings.Args, settings.MaxAttempts,
            settings.Analyze, settings.Scan, settings.Gui, timeoutMs, cancellation).ConfigureAwait(false);
        return 0;
    }
}
