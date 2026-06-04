namespace LTAI.TUI.Commands;

/// <summary>Base record for all parsed slash commands. Zero UI dependency.</summary>
public abstract record Command;

// ── Simple commands (no args) ──

public sealed record HelpCommand : Command;
public sealed record ExitCommand : Command;
public sealed record NewSessionCommand : Command;
public sealed record RetryCommand : Command;
public sealed record CompactCommand : Command;
public sealed record StatusCommand : Command;
public sealed record CostCommand : Command;
public sealed record PwdCommand : Command;
public sealed record ApproveCommand : Command;
public sealed record PlanCommand : Command;
public sealed record UndoCommand : Command;
public sealed record ModelsCommand : Command;

// ── Commands with optional args string ──

public sealed record ModelCommand(string Args) : Command;
public sealed record JobsCommand(string Args) : Command;
public sealed record ConfigCommand(string Args) : Command;
public sealed record SnippetCommand(string Args) : Command;
public sealed record WorkflowCommand(string Args) : Command;
public sealed record PipeCommand(string Args) : Command;
public sealed record ModeCommand(string Args) : Command;
public sealed record LsCommand(string Args) : Command;
public sealed record CdCommand(string Args) : Command;
public sealed record LangCommand(string Args) : Command;
public sealed record SkillCommand(string Args) : Command;

// ── Non-command / unknown ──

/// <summary>Input that is NOT a slash command (normal chat message).</summary>
public sealed record ChatMessageCommand(string Text) : Command;

/// <summary>Unrecognized command with optional fuzzy-match suggestion.</summary>
public sealed record UnknownCommand(string CmdName, string? Suggestion = null) : Command;

/// <summary>Empty or whitespace input.</summary>
public sealed record EmptyCommand : Command;
