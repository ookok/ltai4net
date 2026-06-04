namespace LTAI.TUI.Services;

/// <summary>Structured return value for command execution — no more stringly-typed status messages.</summary>
public abstract record CommandResult;

/// <summary>Normal completion with markup to display as status.</summary>
/// <param name="Markup">Spectre.Console markup string to show in status bar.</param>
/// <param name="SnippetFill">Optional content to fill into input buffer (used by /snippet use).</param>
public sealed record SuccessResult(string Markup, string? SnippetFill = null) : CommandResult;

/// <summary>User wants to exit the application.</summary>
public sealed record ExitResult : CommandResult;

/// <summary>Open a cascade menu for interactive sub-command selection.</summary>
public sealed record CascadeResult(string Cmd, string? Args) : CommandResult;

/// <summary>Redirect the input buffer to a different command string.</summary>
public sealed record RedirectResult(string Input) : CommandResult;
