using Spectre.Console.Cli;

namespace LTAI.Cli.Commands;

public sealed class CompatCommand : AsyncCommand<CompatCommand.Settings>
{
    public sealed class Settings : CommandSettings { }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        return await CompatibilityGate.RunAsync(Array.Empty<string>());
    }
}
