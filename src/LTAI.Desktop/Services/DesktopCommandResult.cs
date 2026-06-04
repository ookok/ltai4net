namespace LTAI.Desktop.Services;

public sealed record DesktopCommandResult(
    string? StatusMessage,
    bool RequestExit = false,
    bool ClearMessages = false
);
