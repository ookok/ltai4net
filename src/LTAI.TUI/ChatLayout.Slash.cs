namespace LTAI.TUI;

using LTAI.TUI.Services;
using static LTAI.TUI.Services.ThemeService;

partial class ChatLayout
{
    internal async Task<bool> HandleSlashCommandAsync(string input)
    {
        if (string.Equals(input, "/new", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, "/clear", StringComparison.OrdinalIgnoreCase))
        {
            await SaveSessionAsync().ConfigureAwait(false);
            SnapshotForUndo();
            lock (_historyLock) _history.Clear();
            _toolCalls.Clear();
            _sessions.NewSession();
            return true;
        }
        if (input.StartsWith("/sessions", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("/session", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSessionsCommandAsync(input).ConfigureAwait(false);
            return true;
        }
        if (input.StartsWith("/theme", StringComparison.OrdinalIgnoreCase))
        {
            ThemeService.Toggle();
            var mode = ThemeService.IsLight ? "浅色" : "深色";
            lock (_historyLock) _history.Add(("cmd", null, $"[green]已切换为 {mode} 主题[/]", null));
            return true;
        }

        if (string.Equals(input, "/undo", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, "/撤销", StringComparison.OrdinalIgnoreCase))
        {
            if (TryUndo())
                lock (_historyLock) _history.Add(("cmd", null, "[green]已撤销上一步操作[/]", null));
            else
                lock (_historyLock) _history.Add(("cmd", null, "[yellow]没有可撤销的操作[/]", null));
            return true;
        }

        if (string.Equals(input, "/keys", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, "/shortcuts", StringComparison.OrdinalIgnoreCase))
        {
            _pendingChatRequest = "/keys";
            _statusMessage = "打开快捷键一览...";
            return true;
        }

        if (string.Equals(input, "/prompt", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, "/agent-prompt", StringComparison.OrdinalIgnoreCase))
        {
            _pendingChatRequest = "/prompt";
            _statusMessage = "打开 Prompt 编辑器...";
            return true;
        }

        if (string.Equals(input, "/retry", StringComparison.OrdinalIgnoreCase))
        {
            string? lastUserMsg = null;
            lock (_historyLock)
            {
                for (int i = _history.Count - 1; i >= 0; i--)
                {
                    if (_history[i].role == "user")
                    {
                        lastUserMsg = _history[i].rawContent;
                        break;
                    }
                }
            }
            if (lastUserMsg != null)
            {
                _ = Task.Run(async () =>
                {
                    lock (_historyLock) _history.Add(("cmd", null, "[yellow]重试上一轮消息...[/]", null));
                    _pendingChatRequest = lastUserMsg;
                });
                return true;
            }
            lock (_historyLock) _history.Add(("cmd", null, "[yellow]没有可以重试的消息[/]", null));
            return true;
        }

        if (string.Equals(input, "/compact", StringComparison.OrdinalIgnoreCase))
        {
            lock (_historyLock)
            {
                SnapshotForUndo();
                if (_history.Count > 4)
                {
                    var keep = _history.GetRange(_history.Count - 4, 4);
                    _history.Clear();
                    _history.AddRange(keep);
                    _history.Add(("cmd", null, "[green]已压缩历史，保留最近 2 轮对话[/]", null));
                }
                else
                {
                    _history.Add(("cmd", null, "[yellow]消息数不足，无需压缩[/]", null));
                }
            }
            return true;
        }

        if (string.Equals(input, "/exit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, "/quit", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _lastCmdTime = DateTime.UtcNow;
        var (handled, cmdStatus) = await SlashCommands.TryExecuteAsync(input).ConfigureAwait(false);
        if (handled)
        {
            if (!string.IsNullOrEmpty(cmdStatus))
                lock (_historyLock) _history.Add(("cmd", null, cmdStatus, null));
            return true;
        }
        return true;
    }

    private async Task HandleSessionsCommandAsync(string input)
    {
        var result = await _sessionHandler.ExecuteAsync(input, SaveSessionAsync).ConfigureAwait(false);

        if (result.LoadedMessages != null) SnapshotForUndo();
        foreach (var msg in result.HistoryMessages)
            _history.Add(("cmd", null, msg, null));

        if (result.LoadedMessages != null)
        {
            _history.Clear();
            _toolCalls.Clear();
            foreach (var (role, content) in result.LoadedMessages)
                _history.Add((role, null, content, null));
        }
    }

    private async Task SaveSessionAsync()
    {
        if (_sessions.CurrentHandle == null) return;
        await _sessions.SaveSessionAsync().ConfigureAwait(false);
        SaveInputHistory();
    }

    private string InputHistoryPath =>
        Path.Combine(Environment.CurrentDirectory, ".livingtree", "input_history.json");

    private void LoadInputHistory()
    {
        try
        {
            var path = InputHistoryPath;
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var items = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
            if (items != null)
            {
                _inputHistory.Clear();
                _inputHistory.AddRange(items);
                if (_inputHistory.Count > 50)
                    _inputHistory.RemoveRange(0, _inputHistory.Count - 50);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ChatLayout] LoadInputHistory: {ex.Message}"); }
    }

    private void SaveInputHistory()
    {
        try
        {
            var dir = Path.GetDirectoryName(InputHistoryPath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = System.Text.Json.JsonSerializer.Serialize(_inputHistory.TakeLast(50).ToList());
            File.WriteAllText(InputHistoryPath, json);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ChatLayout] SaveInputHistory: {ex.Message}"); }
    }
}
