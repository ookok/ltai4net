using LTAI.Core.Commands;

namespace LTAI.TUI.Services;

/// <summary>
/// Marker interface for command service classes.
/// Each service handles one or more related Command types.
/// </summary>
public interface ICommandService
{
    CommandResult Execute(Command command);
}
