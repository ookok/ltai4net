using System.ComponentModel;
using Spectre.Console.Cli;

namespace LTAI.Cli.Commands;

public class GlobalSettings : CommandSettings
{
    [CommandOption("--verbose")]
    [Description("Enable verbose output")]
    [DefaultValue(false)]
    public bool Verbose { get; init; }
}
