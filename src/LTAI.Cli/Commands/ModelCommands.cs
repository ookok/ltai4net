using System.ComponentModel;
using LTAI.Cli.Model;
using Spectre.Console.Cli;

namespace LTAI.Cli.Commands;

public sealed class ModelListCommand : AsyncCommand<ModelListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--layer")]
        [Description("Filter by layer (L0, L1, L2)")]
        public string? Layer { get; init; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        return ModelMode.RunAsync("list", settings.Layer, null, false, false);
    }
}

public sealed class ModelDownloadCommand : AsyncCommand<ModelDownloadCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--layer")]
        [Description("Target layer (L0, L1, L2)")]
        public string? Layer { get; init; }

        [CommandOption("--version")]
        [Description("Model version to download (required)")]
        public string? Version { get; init; }

        [CommandOption("--mirror")]
        [Description("Use hf-mirror.com for faster download")]
        [DefaultValue(false)]
        public bool UseMirror { get; init; }

        [CommandOption("--force")]
        [Description("Force re-download even if already installed")]
        [DefaultValue(false)]
        public bool Force { get; init; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        return ModelMode.RunAsync("download", settings.Layer, settings.Version, settings.UseMirror, settings.Force);
    }
}

public sealed class ModelRemoveCommand : AsyncCommand<ModelRemoveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--layer")]
        [Description("Target layer (L0, L1, L2)")]
        public string? Layer { get; init; }

        [CommandOption("--version")]
        [Description("Model version to remove (required)")]
        public string? Version { get; init; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        return ModelMode.RunAsync("remove", settings.Layer, settings.Version, false, false);
    }
}

public sealed class ModelResetCommand : AsyncCommand<ModelResetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--setup")]
        [Description("Re-run setup wizard after reset")]
        [DefaultValue(false)]
        public bool RerunSetup { get; init; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        return ModelMode.RunAsync("reset", null, null, false, false, settings.RerunSetup);
    }
}
