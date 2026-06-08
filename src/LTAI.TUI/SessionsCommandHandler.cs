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
            "export" => ExportSession(arg),
            "import" => await ImportSessionAsync(arg),
            "search" => await SearchSessionsAsync(arg),
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

    private SessionResult ExportSession(string arg)
    {
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var name = parts.Length > 0 ? parts[0] : _sessions.CurrentSession;
        var format = parts.Length > 1 ? parts[1].ToLowerInvariant() : "md";

        if (string.IsNullOrEmpty(name))
            return new SessionResult(["[yellow]用法: /sessions export [<name>] [md|json|html][/]"], IsError: true);

        var handle = _sessions.LoadSession(name);
        if (handle == null)
            return new SessionResult([$"[red]找不到会话 '{name}'[/]"], IsError: true);

        try
        {
            var ext = format switch { "json" => "json", "html" => "html", _ => "md" };
            var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            var filePath = Path.Combine(Environment.CurrentDirectory, ".livingtree",
                $"session-{safeName}.{ext}");
            var dir = Path.GetDirectoryName(filePath);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var content = format switch
            {
                "json" => handle.SerializeToJson(),
                "html" => BuildHtmlExport(handle),
                _ => BuildMarkdownExport(handle),
            };
            File.WriteAllText(filePath, content);
            return new SessionResult([$"[green]已导出会话 '{name}' → {filePath}[/]"]);
        }
        catch (Exception ex)
        {
            return new SessionResult([$"[red]导出失败: {ex.Message}[/]"], IsError: true);
        }
    }

    private async Task<SessionResult> ImportSessionAsync(string arg)
    {
        if (string.IsNullOrEmpty(arg))
            return new SessionResult(["[yellow]用法: /sessions import <文件路径>[/]"], IsError: true);

        try
        {
            if (!File.Exists(arg))
                return new SessionResult([$"[red]文件不存在: {arg}[/]"], IsError: true);

            var json = await File.ReadAllTextAsync(arg).ConfigureAwait(false);
            var handle = _sessions.LoadSession(arg);
            if (handle != null)
            {
                handle.UpdateFromJson(json);
                _sessions.SaveSession(handle);
                return new SessionResult([$"[green]已导入会话: {handle.Name}[/]"]);
            }

            // Try creating a new session from content
            var newHandle = _sessions.NewSession();
            newHandle.UpdateFromJson(json);
            _sessions.SaveSession(newHandle);
            return new SessionResult([$"[green]已导入为新会话: {newHandle.Name}[/]"]);
        }
        catch (Exception ex)
        {
            return new SessionResult([$"[red]导入失败: {ex.Message}[/]"], IsError: true);
        }
    }

    private async Task<SessionResult> SearchSessionsAsync(string query)
    {
        if (string.IsNullOrEmpty(query))
            return new SessionResult(["[yellow]用法: /sessions search <关键词>[/]"], IsError: true);

        var sessions = _sessions.ListSessions();
        var results = new List<(string name, int matches)>();

        foreach (var s in sessions)
        {
            if (s.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add((s.Name, -1));
                continue;
            }
            var handle = _sessions.LoadSession(s.Name);
            if (handle == null) continue;
            var matchCount = handle.Messages.Count(m =>
                m.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
            if (matchCount > 0)
                results.Add((s.Name, matchCount));
        }

        if (results.Count == 0)
            return new SessionResult([$"[yellow]未找到包含 '{query}' 的会话[/]"]);

        var lines = new List<string> { $"[bold yellow]搜索 '{query}' 结果 ({results.Count}):[/]" };
        foreach (var (name, count) in results.Take(20))
        {
            var detail = count >= 0 ? $" [dim]({count} 条匹配)[/]" : " [dim](名称匹配)[/]";
            lines.Add($"  [cyan]{name.EscapeMarkup(),-22}[/]{detail}");
        }
        if (results.Count > 20)
            lines.Add($"[grey]... 还有 {results.Count - 20} 个匹配[/]");
        return new SessionResult(lines);
    }

    private static string BuildMarkdownExport(ISessionHandle handle)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# 会话: {handle.Name}");
        sb.AppendLine($"- 导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 消息数: {handle.Messages.Count}");
        sb.AppendLine();
        foreach (var m in handle.Messages)
        {
            var role = m.Role == Microsoft.Extensions.AI.ChatRole.User ? "**用户**" : "**AI**";
            sb.AppendLine($"## {role}");
            sb.AppendLine();
            sb.AppendLine(m.Text ?? "");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string BuildHtmlExport(ISessionHandle handle)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>会话: {HtmlEncode(handle.Name)}</title>");
        sb.AppendLine("<style>body{max-width:800px;margin:auto;padding:20px;font-family:sans-serif}");
        sb.AppendLine(".msg{border:1px solid #ddd;border-radius:8px;padding:12px;margin:8px 0}");
        sb.AppendLine(".user{background:#e3f2fd}.ai{background:#f3e5f5}</style></head><body>");
        sb.AppendLine($"<h1>会话: {HtmlEncode(handle.Name)}</h1>");
        foreach (var m in handle.Messages)
        {
            var cls = m.Role == Microsoft.Extensions.AI.ChatRole.User ? "user" : "ai";
            sb.AppendLine($"<div class=\"msg {cls}\"><pre>{HtmlEncode(m.Text ?? "")}</pre></div>");
        }
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string HtmlEncode(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&#39;");

    private static SessionResult DefaultUsage() =>
        new(["[yellow]用法: /sessions list|load <name>|delete <name>|export [<name>] [md|json|html]|import <path>|search <query>[/]"]);
}
