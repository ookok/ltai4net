using LTAI.Core.Session;
using Microsoft.Extensions.AI;
using Spectre.Console;

namespace LTAI.TUI;

/// <summary>
/// Processes /sessions commands (list/load/delete) and returns structured results.
/// Keeps ChatLayout free of session management logic.
/// </summary>
public sealed class SessionsCommandHandler
{
    private readonly SessionManager _sessions;

    public SessionsCommandHandler(SessionManager sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public void NewSession() => _sessions.NewSession();

    public sealed record SessionResult(
        IReadOnlyList<string> HistoryMessages,
        IReadOnlyList<(string role, string content)>? LoadedMessages = null,
        bool IsError = false);

    public async Task<SessionResult> ExecuteAsync(string input, Func<Task> saveCurrentSession)
    {
        var parts = input.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        var arg = parts.Length > 2 ? parts[2] : "";

        return sub switch
        {
            "list" or "ls" or "" => await ListSessionsAsync(),
            "load" => await LoadSessionAsync(arg, saveCurrentSession),
            "delete" or "rm" => DeleteSession(arg),
            _ => DefaultUsage(),
        };
    }

    private Task<SessionResult> ListSessionsAsync()
    {
        var sessions = _sessions.ListSessions();
        if (sessions.Length == 0)
            return Task.FromResult(new SessionResult([
                "[yellow]📋 暂无已保存的会话[/]"
            ]));

        var lines = new List<string> { "[bold yellow]📋 已保存的会话:[/]" };
        foreach (var s in sessions.Take(20))
        {
            var marker = s.Name == _sessions.CurrentSession ? " [green]← 当前[/]" : "";
            lines.Add($"  [cyan]{s.DisplayName,-22}[/]{marker}");
        }
        if (sessions.Length > 20)
            lines.Add($"[grey]  ... 还有 {sessions.Length - 20} 个[/]");
        lines.Add("[dim]使用 /sessions load <name> 加载[/]");
        return Task.FromResult(new SessionResult(lines));
    }

    private async Task<SessionResult> LoadSessionAsync(string arg, Func<Task> saveCurrentSession)
    {
        if (string.IsNullOrEmpty(arg))
            return new SessionResult(["[yellow]用法: /sessions load <会话名>[/]"], IsError: true);

        await saveCurrentSession().ConfigureAwait(false);
        var handle = _sessions.LoadSession(arg);
        if (handle == null)
            return new SessionResult([$"[red]❌ 找不到会话 '{arg}'。使用 /sessions list 查看[/]"], IsError: true);

        var messages = new List<(string role, string content)>();
        foreach (var m in handle.Messages)
        {
            var role = m.Role == ChatRole.User ? "user" : "assistant";
            messages.Add((role, m.Text ?? ""));
        }
        messages.Add(("cmd", $"[green]✅ 已加载会话: {SessionManager.FormatSessionName(arg)}[/]"));

        return new SessionResult([], LoadedMessages: messages);
    }

    private SessionResult DeleteSession(string arg)
    {
        if (string.IsNullOrEmpty(arg))
            return new SessionResult(["[yellow]用法: /sessions delete <会话名>[/]"], IsError: true);

        _sessions.DeleteSession(arg);
        return new SessionResult([$"[green]✅ 已删除会话: {arg}[/]"]);
    }

    private static SessionResult DefaultUsage() =>
        new(["[yellow]用法: /sessions list|load <name>|delete <name>[/]"]);
}
