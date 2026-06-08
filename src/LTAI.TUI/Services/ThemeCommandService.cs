using LTAI.Core.Commands;

namespace LTAI.TUI.Services;

public sealed class ThemeCommandService : ICommandService
{
    public Task<CommandResult> ExecuteAsync(Command command) => command switch
    {
        ThemeCommand => Task.FromResult(HandleTheme()),
        _ => Task.FromResult<CommandResult>(new SuccessResult("ok")),
    };

    private static CommandResult HandleTheme()
    {
        ThemeService.Toggle();
        var mode = ThemeService.IsLight ? "浅色" : "深色";
        return new SuccessResult($"[green]已切换为 {mode} 主题[/]");
    }
}
