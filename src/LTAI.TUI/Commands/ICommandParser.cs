namespace LTAI.TUI.Commands;

/// <summary>
/// Parses raw input text into a typed <see cref="Command"/> record.
/// 100% testable — no UI, no DI, no static state.
/// </summary>
public interface ICommandParser
{
    /// <summary>
    /// Parse a single line of user input.
    /// Returns <see cref="ChatMessageCommand"/> if input does not start with '/'.
    /// Returns <see cref="EmptyCommand"/> for whitespace/empty input.
    /// Returns <see cref="UnknownCommand"/> for unrecognized commands (with optional fuzzy suggestion).
    /// </summary>
    Command Parse(string input);
}
