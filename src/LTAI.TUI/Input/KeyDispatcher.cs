using LTAI.Agent.Tools;
using LTAI.TUI;
using Spectre.Console;
using System.Text;
using System.Threading.Channels;

namespace LTAI.TUI.Input;

/// <summary>Agent TUI input states.</summary>
public enum InputState
{
    /// <summary>Multi-line text editing.</summary>
    Normal,
    /// <summary>Command suggestion picker (/).</summary>
    Picker,
    /// <summary>Cascading command navigation.</summary>
    Cascade,
}

/// <summary>
/// State-machine key dispatcher for the ChatLayout input loop.
/// Routes every keystroke to the correct handler based on current state.
/// </summary>
public sealed class KeyDispatcher
{
    private readonly ChatLayout _owner;
    public InputState State { get; private set; } = InputState.Normal;

    public KeyDispatcher(ChatLayout owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// Process one keystroke. Returns true to continue the input loop,
    /// false to exit (quit).
    /// </summary>
    public async Task<bool> HandleKeyAsync(ConsoleKeyInfo key, CancellationToken ct)
    {
        // Cascade menu has highest priority
        if (SlashCommands.InCascadeMenu)
            return HandleCascade(key, ct);

        // Picker state
        if (State == InputState.Picker)
            return await HandlePickerAsync(key, ct);

        // Normal / default state
        return await HandleNormalAsync(key, ct);
    }

    // ═══════════════════════════════════════════════
    //  Normal state
    // ═══════════════════════════════════════════════

    private async Task<bool> HandleNormalAsync(ConsoleKeyInfo key, CancellationToken ct)
    {
        // View switch (empty input only)
        if (_owner.IsInputEmpty() && "013456789".Contains(key.KeyChar))
        {
            _owner._quickNav = key.KeyChar;
            return true;
        }

        // Esc → cancel AI or quit
        if (key.Key == ConsoleKey.Escape || key.KeyChar == 'q')
        {
            if (_owner._processing) { _owner._responseCts?.Cancel(); return true; }
            return false;
        }

        // Ctrl+Shift+C → copy latest code block to clipboard
        if (key.Key == ConsoleKey.C && Mods(key, ConsoleModifiers.Control | ConsoleModifiers.Shift))
        {
            var block = Rendering.CodeBlockBuffer.PeekLatest();
            if (block != null)
            {
                try
                {
                    TextCopy.ClipboardService.SetText(block.Value.code);
                    _owner._statusMessage = $"[green]已复制 {block.Value.lang ?? "code"} 块 ({block.Value.code.Split('\n').Length} 行)[/]";
                }
                catch { _owner._statusMessage = "[red]复制失败[/]"; }
            }
            else
            {
                _owner._statusMessage = "[yellow]没有可复制的代码块[/]";
            }
            return true;
        }

        // Ctrl+E → 切换最新 AI 消息的推理过程展开/折叠
        if (key.Key == ConsoleKey.E && Mods(key, ConsoleModifiers.Control))
        {
            for (int i = _owner._history.Count - 1; i >= 0; i--)
            {
                if (_owner._history[i].role is "assistant" or "ai")
                {
                    if (!string.IsNullOrEmpty(_owner._history[i].reasoning))
                    {
                        if (_owner._expandedMessages.Contains(i))
                            _owner._expandedMessages.Remove(i);
                        else
                            _owner._expandedMessages.Add(i);
                        _owner.InvalidateRendered();
                    }
                    break;
                }
            }
            return true;
        }

        // Ctrl+L → clear
        if (key.Key == ConsoleKey.L && Mods(key, ConsoleModifiers.Control))
        {
            _owner._history.Clear();
            _owner._toolCalls.Clear();
            _owner._scrollOffset = 0;
            _owner.InvalidateRendered();
            return true;
        }

        // Alt+Shift+/ → Command Palette (context-aware quick prompt)
        if (key.Key == ConsoleKey.Oem2 && Mods(key, ConsoleModifiers.Alt | ConsoleModifiers.Shift))
        {
            AnsiConsole.Markup("[bold yellow]> [/]");
            var paletteInput = Console.ReadLine() ?? "";
            if (!string.IsNullOrWhiteSpace(paletteInput))
            {
                var context = "";
                var currentFile = _owner.CurrentFileForContext;
                if (currentFile != null)
                    context = $"[当前文件: {currentFile}]\n";
                _owner.EnqueueUserMessage($"{context}[命令面板] {paletteInput}");
            }
            return true;
        }

        // Ctrl+F → inline search in current input
        if (key.Key == ConsoleKey.F && Mods(key, ConsoleModifiers.Control))
        {
            try
            {
                AnsiConsole.Markup("[bold yellow]🔍 搜索: [/]");
                var searchTerm = Console.ReadLine() ?? "";
                if (string.IsNullOrWhiteSpace(searchTerm)) return true;

                var fullText = string.Join("\n", _owner._inputLines);
                var idx = fullText.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    _owner._statusMessage = $"[yellow]未找到 '{searchTerm.EscapeMarkup()}'[/]";
                    return true;
                }

                // Find line and column
                var lineIdx = 0;
                var charCount = 0;
                for (int i = 0; i < _owner._inputLines.Count; i++)
                {
                    if (charCount + _owner._inputLines[i].Length + 1 > idx)
                    {
                        _owner._cursorLine = i;
                        _owner._cursorCol = idx - charCount;
                        break;
                    }
                    charCount += _owner._inputLines[i].Length + 1;
                    lineIdx++;
                }
                _owner._statusMessage = $"[green]找到 '{searchTerm.EscapeMarkup()}' (行 {_owner._cursorLine + 1}, 列 {_owner._cursorCol + 1})[/]";
            }
            catch { }
            return true;
        }

        // Ctrl+V → paste (with preview if > 3 lines)
        if (key.Key == ConsoleKey.V && Mods(key, ConsoleModifiers.Control))
        {
            try
            {
                var clip = TextCopy.ClipboardService.GetText() ?? "";
                var clipLines = clip.Split('\n');
                if (clipLines.Length > 3)
                {
                    AnsiConsole.Clear();
                    AnsiConsole.Write(new Rule("[bold yellow]📋 粘贴预览[/]"));
                    var preview = string.Join("\n", clipLines.Take(5));
                    if (clipLines.Length > 5) preview += $"\n[grey]... 还有 {clipLines.Length - 5} 行[/]";
                    AnsiConsole.Write(new Panel(new Markup(preview.EscapeMarkup()))
                        .Border(BoxBorder.Rounded)
                        .Header($"[bold] {clip.Length} 字符, {clipLines.Length} 行 [/]")
                        .Expand());
                    AnsiConsole.Markup("\n[yellow]确认粘贴? (Enter=粘贴, Esc=取消): [/]");
                    var confirm = Console.ReadKey(true);
                    if (confirm.Key == ConsoleKey.Escape) return true;
                }
                foreach (var cl in clipLines)
                    _owner._inputLines.Insert(_owner._cursorLine++, cl);
                _owner._cursorLine--;
                _owner._cursorCol = _owner._inputLines[_owner._cursorLine].Length;
                while (_owner._inputLines.Count > ChatLayout.MaxInputLines)
                    _owner._inputLines.RemoveAt(0);
            }
            catch { }
            return true;
        }

        // Ctrl+↑ → history up
        if (key.Key == ConsoleKey.UpArrow && Mods(key, ConsoleModifiers.Control))
        {
            if (_owner._inputHistory.Count > 0)
            {
                if (_owner._historyIndex < 0)
                    _owner._historyIndex = _owner._inputHistory.Count - 1;
                else if (_owner._historyIndex > 0)
                    _owner._historyIndex--;
                _owner.ReplaceInputLine(_owner._inputHistory[_owner._historyIndex]);
            }
            return true;
        }

        // Ctrl+↓ → history down
        if (key.Key == ConsoleKey.DownArrow && Mods(key, ConsoleModifiers.Control))
        {
            if (_owner._historyIndex >= 0)
            {
                _owner._historyIndex++;
                if (_owner._historyIndex >= _owner._inputHistory.Count)
                {
                    _owner._historyIndex = -1;
                    _owner.ClearInput();
                }
                else
                {
                    _owner.ReplaceInputLine(_owner._inputHistory[_owner._historyIndex]);
                }
            }
            return true;
        }

        // Shift+↑↓ → scroll output
        if (Mods(key, ConsoleModifiers.Shift))
        {
            if (key.Key == ConsoleKey.UpArrow && _owner._scrollOffset < _owner._history.Count - 1)
                _owner._scrollOffset++;
            else if (key.Key == ConsoleKey.DownArrow && _owner._scrollOffset > 0)
                _owner._scrollOffset--;
            return true;
        }

        // PgUp/PgDn → fast scroll
        if (key.Key == ConsoleKey.PageUp && _owner._scrollOffset < _owner._history.Count - 1)
        {
            _owner._scrollOffset = Math.Min(_owner._scrollOffset + 3, Math.Max(0, _owner._history.Count - 1));
            return true;
        }
        if (key.Key == ConsoleKey.PageDown)
        {
            _owner._scrollOffset = Math.Max(0, _owner._scrollOffset - 3);
            return true;
        }

        // ↑↓ → cursor move in input
        if (key.Key == ConsoleKey.UpArrow)
        {
            if (_owner._cursorLine > 0)
            {
                _owner._cursorLine--;
                _owner._cursorCol = Math.Min(_owner._cursorCol, _owner._inputLines[_owner._cursorLine].Length);
            }
            return true;
        }
        if (key.Key == ConsoleKey.DownArrow)
        {
            if (_owner._cursorLine < _owner._inputLines.Count - 1)
            {
                _owner._cursorLine++;
                _owner._cursorCol = Math.Min(_owner._cursorCol, _owner._inputLines[_owner._cursorLine].Length);
            }
            return true;
        }

        // ← → horizontal
        if (key.Key == ConsoleKey.LeftArrow)
        {
            if (_owner._cursorCol > 0) _owner._cursorCol--;
            return true;
        }
        if (key.Key == ConsoleKey.RightArrow)
        {
            if (_owner._cursorCol < _owner._inputLines[_owner._cursorLine].Length) _owner._cursorCol++;
            return true;
        }

        // Home / End
        if (key.Key == ConsoleKey.Home) { _owner._cursorCol = 0; return true; }
        if (key.Key == ConsoleKey.End) { _owner._cursorCol = _owner._inputLines[_owner._cursorLine].Length; return true; }

        // Enter → newline (max MaxInputLines)
        if (key.Key == ConsoleKey.Enter && !Mods(key, ConsoleModifiers.Shift))
        {
            var line = _owner._inputLines[_owner._cursorLine];
            var remainder = line[_owner._cursorCol..];
            _owner._inputLines[_owner._cursorLine] = line[.._owner._cursorCol];
            _owner._inputLines.Insert(_owner._cursorLine + 1, remainder);
            _owner._cursorLine++;
            _owner._cursorCol = 0;
            if (_owner._inputLines.Count > ChatLayout.MaxInputLines)
            {
                _owner._inputLines.RemoveRange(0, _owner._inputLines.Count - ChatLayout.MaxInputLines);
                _owner._cursorLine = _owner._inputLines.Count - 1;
            }
            return true;
        }

        // Shift+Enter → send
        if (key.Key == ConsoleKey.Enter && Mods(key, ConsoleModifiers.Shift))
        {
            var input = _owner.GetInputText().Trim();
            _owner.ClearInput();
            _owner._scrollOffset = 0;
            if (string.IsNullOrEmpty(input)) return true;

            _owner.SaveToHistory(input);
            _owner._historyIndex = -1;

            if (input.StartsWith('/'))
            {
                var handled = await _owner.HandleSlashCommandAsync(input).ConfigureAwait(false);
                if (!handled) return false;
                var pending = SlashCommands.PendingInput;
                if (pending != null)
                {
                    SlashCommands.PendingInput = null;
                    _owner.SetInput(pending);
                }
                return true;
            }

            lock (_owner._history) _owner._history.Add(("user", null, input, null));
            _owner.TrimHistory();
            await _owner._messageQueue.Writer.WriteAsync(input, ct).ConfigureAwait(false);
            return true;
        }

        // Backspace
        if (key.Key == ConsoleKey.Backspace)
        {
            _owner.BackspaceChar();
            if (_owner._pickerActive && _owner._inputLines.Count == 1 && _owner._inputLines[0].Length <= 1)
                _owner._pickerActive = false;
            return true;
        }

        // Delete
        if (key.Key == ConsoleKey.Delete)
        {
            var line = _owner._inputLines[_owner._cursorLine];
            if (_owner._cursorCol < line.Length)
                _owner._inputLines[_owner._cursorLine] = line.Remove(_owner._cursorCol, 1);
            else if (_owner._cursorLine < _owner._inputLines.Count - 1)
            {
                var nextLine = _owner._inputLines[_owner._cursorLine + 1];
                _owner._inputLines[_owner._cursorLine] = line + nextLine;
                _owner._inputLines.RemoveAt(_owner._cursorLine + 1);
            }
            return true;
        }

        // Typing
        if (!char.IsControl(key.KeyChar))
        {
            _owner.InsertChar(key.KeyChar);
            if (_owner._inputLines.Count > ChatLayout.MaxInputLines)
            {
                _owner._inputLines.RemoveRange(0, _owner._inputLines.Count - ChatLayout.MaxInputLines);
                _owner._cursorLine = _owner._inputLines.Count - 1;
            }
            _owner.CheckPickerTrigger();
        }

        return true;
    }

    // ═══════════════════════════════════════════════
    //  Picker state
    // ═══════════════════════════════════════════════

    private async Task<bool> HandlePickerAsync(ConsoleKeyInfo key, CancellationToken ct)
    {
        string? pickerResult = null;
        bool pickerDone = false;

        lock (_owner._pickerLock)
        {
            if (key.Key == ConsoleKey.UpArrow || (key.Key == ConsoleKey.K && _owner._pickerFilter.Length == 0))
            {
                if (_owner._pickerItems.Count > 0)
                    _owner._pickerSelectedIdx = (_owner._pickerSelectedIdx - 1 + _owner._pickerItems.Count) % _owner._pickerItems.Count;
            }
            else if (key.Key == ConsoleKey.DownArrow || (key.Key == ConsoleKey.J && _owner._pickerFilter.Length == 0))
            {
                if (_owner._pickerItems.Count > 0)
                    _owner._pickerSelectedIdx = (_owner._pickerSelectedIdx + 1) % _owner._pickerItems.Count;
            }
            else if (key.Key == ConsoleKey.Enter)
            {
                if (_owner._pickerSelectedIdx >= 0 && _owner._pickerSelectedIdx < _owner._pickerItems.Count)
                    pickerResult = _owner._pickerItems[_owner._pickerSelectedIdx].Completion;
                pickerDone = true;
            }
            else if (key.Key == ConsoleKey.Escape || key.KeyChar == 'q')
            {
                pickerDone = true;
            }
            else if (key.Key == ConsoleKey.Backspace)
            {
                if (_owner._pickerFilter.Length > 0)
                    _owner._pickerFilter = _owner._pickerFilter[..^1];
                _owner.UpdatePickerItems();
            }
            else if (key.Key == ConsoleKey.Tab)
            {
                var completions = _owner._pickerItems
                    .Select(s => s.Completion)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (completions.Count == 1)
                {
                    pickerResult = completions[0] + " ";
                    pickerDone = true;
                }
                else if (completions.Count > 1)
                {
                    var lcp = ProviderHelpers.LongestCommonPrefix(completions);
                    if (lcp.Length > ("/" + _owner._pickerFilter).Length)
                        _owner._pickerFilter = lcp.Length > 1 ? lcp[1..] : "";
                    _owner.UpdatePickerItems();
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                _owner._pickerFilter += key.KeyChar;
                _owner.UpdatePickerItems();
            }
        }

        if (pickerDone)
        {
            lock (_owner._pickerLock)
            {
                _owner._pickerActive = false;
                State = InputState.Normal;
                _owner._pickerFilter = "";
                _owner._pickerItems = new();
                _owner._pickerSelectedIdx = -1;
            }
            _owner.ClearInput();
            if (pickerResult != null)
            {
                var handled = await _owner.HandleSlashCommandAsync(pickerResult).ConfigureAwait(false);
                if (!handled) return false;
            }
        }

        return true;
    }

    // ═══════════════════════════════════════════════
    //  Cascade state
    // ═══════════════════════════════════════════════

    private bool HandleCascade(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (key.Key == ConsoleKey.Q && Mods(key, ConsoleModifiers.Control))
        {
            SlashCommands.CloseCascadeMenu();
            _owner._history.RemoveAll(x => x.role == "cmd");
            if (_owner._history.Count > 0)
                _owner._history.Add(("cmd", null, "[dim]───[/]", null));
            _owner.InvalidateRendered();
            return true;
        }

        var stillIn = SlashCommands.HandleCascadeKey(key);
        if (!stillIn)
        {
            var p = SlashCommands.PendingInput;
            if (p != null)
            {
                SlashCommands.PendingInput = null;
                _owner.SetInput(p);
            }
            _owner._history.RemoveAll(x => x.role == "cmd");
            if (_owner._history.Count > 0)
                _owner._history.Add(("cmd", null, "[dim]───[/]", null));
            _owner.InvalidateRendered();
            return true;
        }

        // Update cascade display
        lock (_owner._history)
        {
            if (_owner._history.Count > 0 && _owner._history[^1].role == "cmd")
                _owner._history[^1] = ("cmd", null, SlashCommands.BuildCascadeText(), null);
        }
        _owner.InvalidateRendered();
        return true;
    }

    // ═══════════════════════════════════════════════
    //  Utilities
    // ═══════════════════════════════════════════════

    private static bool Mods(ConsoleKeyInfo key, ConsoleModifiers mod) =>
        (key.Modifiers & mod) != 0;

}
