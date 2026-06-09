using System;
using System.Threading.Tasks;

namespace LTAI.Agent.Tools;

/// <summary>
/// Cross-project bridge: TUI/Desktop sets PromptAsync at startup.
/// Tools call PromptAsync for inline user input (replaces AnsiConsole.Prompt).
/// </summary>
public static class UserInputService
{
    /// <summary>Set by TUI/Desktop. Returns user input or null if cancelled.</summary>
    public static Func<string, bool, Task<string?>>? PromptAsync { get; set; }
}
