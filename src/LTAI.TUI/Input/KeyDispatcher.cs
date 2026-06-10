using LTAI.Agent.Tools;
using LTAI.Core.Configuration;
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

public sealed class KeyDispatcher
{
    private readonly ChatLayout _owner;
    public InputState State { get; private set; } = InputState.Normal;

    private delegate Task<bool> KeyHandler(ConsoleKeyInfo key, CancellationToken ct);

    private readonly Dictionary<(ConsoleKey, ConsoleModifiers), KeyHandler> _modTable = new();
    private readonly Dictionary<ConsoleKey, KeyHandler> _keyTable = new();

    public KeyDispatcher(ChatLayout owner)
    {
        _owner = owner;
        BuildHandlerTable();
    }

    private void BuildHandlerTable()
    {
        _modTable[(ConsoleKey.Escape, 0)] = HandleEscape;
        _modTable[(ConsoleKey.P, ConsoleModifiers.Control)] = (_, _) => { _owner._viewPickerActive = true; return Task.FromResult(true); };
        _modTable[(ConsoleKey.C, ConsoleModifiers.Control | ConsoleModifiers.Shift)] = HandleCopyCodeBlock;
        _modTable[(ConsoleKey.E, ConsoleModifiers.Control)] = HandleToggleReasoning;
        _modTable[(ConsoleKey.L, ConsoleModifiers.Control)] = (_, _) => { _owner._history.Clear(); _owner._toolCalls.Clear(); _owner._scrollOffset = 0; _owner.InvalidateRendered(); return Task.FromResult(true); };
        _modTable[(ConsoleKey.F, ConsoleModifiers.Control)] = HandleInlineSearch;
        _modTable[(ConsoleKey.V, ConsoleModifiers.Control)] = HandlePaste;
        _modTable[(ConsoleKey.UpArrow, ConsoleModifiers.Control)] = HandleHistoryUp;
        _modTable[(ConsoleKey.DownArrow, ConsoleModifiers.Control)] = HandleHistoryDown;
        _modTable[(ConsoleKey.Oem2, ConsoleModifiers.Alt | ConsoleModifiers.Shift)] = HandleCommandPalette;

        _keyTable[ConsoleKey.Enter] = HandleEnter;
        _keyTable[ConsoleKey.Backspace] = HandleBackspace;
        _keyTable[ConsoleKey.Delete] = HandleDelete;
        _keyTable[ConsoleKey.Home] = (_, _) => { _owner._cursorCol = 0; _owner.InvalidateRendered(); return Task.FromResult(true); };
        _keyTable[ConsoleKey.End] = (_, _) => { _owner._cursorCol = _owner._inputLines[_owner._cursorLine].Length; _owner.InvalidateRendered(); return Task.FromResult(true); };
        _keyTable[ConsoleKey.UpArrow] = HandleArrowUp;
        _keyTable[ConsoleKey.DownArrow] = HandleArrowDown;
        _keyTable[ConsoleKey.LeftArrow] = (_, _) => { if (_owner._cursorCol > 0) { _owner._cursorCol--; _owner.InvalidateRendered(); } return Task.FromResult(true); };
        _keyTable[ConsoleKey.RightArrow] = (_, _) => { if (_owner._cursorCol < _owner._inputLines[_owner._cursorLine].Length) { _owner._cursorCol++; _owner.InvalidateRendered(); } return Task.FromResult(true); };
        _keyTable[ConsoleKey.PageUp] = HandlePageUp;
        _keyTable[ConsoleKey.PageDown] = HandlePageDown;
        _keyTable[ConsoleKey.Tab] = HandleTab;
    }

    public async Task<bool> HandleKeyAsync(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (_owner._questionActive)
            return HandleQuestionKey(key, ct);
        if (_owner._textInputActive)
            return HandleTextInputKey(key, ct);
        if (_owner._modelPickerActive)
            return HandleModelPickerKey(key, ct);
        if (SlashCommands.InCascadeMenu)
            return HandleCascade(key, ct);
        if (_owner._viewPickerActive)
            return HandleViewPicker(key, ct);

        // Pending confirmation modal
        if (ChatLayout.PendingConfirmTcs is { Task.IsCompleted: false } tcs)
        {
            if (ConfirmationModal.HandleConfirmKey(key, out var choice, out var selChanged))
                tcs.TrySetResult(choice);
            if (selChanged)
                _owner.ReRenderConfirmation();
            return true;
        }

        if (_owner._pickerActive)
            return await HandlePickerAsync(key, ct);

        // 视图切换快捷键（空输入时）
        if (_owner.IsInputEmpty() && "013456789".Contains(key.KeyChar))
        {
            _owner._quickNav = key.KeyChar;
            return true;
        }

        // Modifier-key combos
        if (key.Modifiers != 0 && _modTable.TryGetValue((key.Key, key.Modifiers), out var modHandler))
            return await modHandler(key, ct);

        // Plain key (no modifier)
        if (key.Modifiers == 0 && _keyTable.TryGetValue(key.Key, out var keyHandler))
            return await keyHandler(key, ct);

        // Shift+↑↓ → scroll
        if (Mods(key, ConsoleModifiers.Shift))
        {
            if (key.Key == ConsoleKey.UpArrow && _owner._scrollOffset < _owner._history.Count - 1)
                _owner._scrollOffset++;
            else if (key.Key == ConsoleKey.DownArrow && _owner._scrollOffset > 0)
                _owner._scrollOffset--;
            return true;
        }

        // Inline search term capture
        if (_owner._pendingSearchTerm != null)
        {
            if (key.Key == ConsoleKey.Escape)
            {
                _owner._pendingSearchTerm = null;
                _owner._statusMessage = null;
            }
            else if (key.Key == ConsoleKey.Enter)
            {
                var searchTerm = _owner._pendingSearchTerm;
                _owner._pendingSearchTerm = null;
                if (!string.IsNullOrWhiteSpace(searchTerm))
                    PerformSearch(searchTerm);
            }
            else if (key.Key == ConsoleKey.Backspace)
            {
                if (_owner._pendingSearchTerm.Length > 0)
                    _owner._pendingSearchTerm = _owner._pendingSearchTerm[..^1];
                _owner._statusMessage = $"[yellow]🔍 搜索: {_owner._pendingSearchTerm}|[/]";
            }
            else if (!char.IsControl(key.KeyChar))
            {
                _owner._pendingSearchTerm += key.KeyChar;
                _owner._statusMessage = $"[yellow]🔍 搜索: {_owner._pendingSearchTerm}|[/]";
            }
            _owner.InvalidateRendered();
            return true;
        }

        // Regular character input
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
    //  Handler implementations
    // ═══════════════════════════════════════════════

    private Task<bool> HandleEscape(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (_owner._uiState == ChatLayout.TuiUiState.Streaming) { _owner._responseCts?.Cancel(); return Task.FromResult(true); }
        return Task.FromResult(false);
    }

    private Task<bool> HandleCopyCodeBlock(ConsoleKeyInfo key, CancellationToken ct)
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
        _owner.InvalidateRendered();
        return Task.FromResult(true);
    }

    private Task<bool> HandleToggleReasoning(ConsoleKeyInfo key, CancellationToken ct)
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
        return Task.FromResult(true);
    }

    private Task<bool> HandleCommandPalette(ConsoleKeyInfo key, CancellationToken ct)
    {
        _owner._pendingChatRequest = "command-palette";
        _owner._statusMessage = "[yellow]⚡ 快速命令: |[/]";
        _owner.InvalidateRendered();
        return Task.FromResult(true);
    }

    private Task<bool> HandleInlineSearch(ConsoleKeyInfo key, CancellationToken ct)
    {
        _owner._pendingSearchTerm = "";
        _owner._statusMessage = "[yellow]🔍 搜索: |[/]";
        _owner.InvalidateRendered();
        return Task.FromResult(true);
    }

    private Task<bool> HandlePaste(ConsoleKeyInfo key, CancellationToken ct)
    {
        try
        {
            var clip = TextCopy.ClipboardService.GetText() ?? "";
            var clipLines = clip.Split('\n');
            foreach (var cl in clipLines)
                _owner._inputLines.Insert(_owner._cursorLine++, cl);
            _owner._cursorLine--;
            _owner._cursorCol = _owner._inputLines[_owner._cursorLine].Length;
            while (_owner._inputLines.Count > ChatLayout.MaxInputLines)
            {
                _owner._inputLines.RemoveAt(0);
                _owner._cursorLine = Math.Max(0, _owner._cursorLine - 1);
            }
            _owner._statusMessage = $"[green]已粘贴 {clip.Length} 字符 ({clipLines.Length} 行)[/]";
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[KeyDispatcher] paste: {ex.Message}"); }
        _owner.InvalidateRendered();
        return Task.FromResult(true);
    }

    private Task<bool> HandleHistoryUp(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (_owner._inputHistory.Count > 0)
        {
            if (_owner._historyIndex < 0)
                _owner._historyIndex = _owner._inputHistory.Count - 1;
            else if (_owner._historyIndex > 0)
                _owner._historyIndex--;
            _owner.ReplaceInputLine(_owner._inputHistory[_owner._historyIndex]);
        }
        return Task.FromResult(true);
    }

    private Task<bool> HandleHistoryDown(ConsoleKeyInfo key, CancellationToken ct)
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
        return Task.FromResult(true);
    }

    private void PerformSearch(string searchTerm)
    {
        var fullText = string.Join("\n", _owner._inputLines);
        var idx = fullText.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            _owner._statusMessage = $"[yellow]未找到 '{searchTerm.EscapeMarkup()}'[/]";
            return;
        }

        var lineStart = 0;
        for (int i = 0; i < _owner._inputLines.Count; i++)
        {
            if (lineStart + _owner._inputLines[i].Length >= idx)
            {
                _owner._cursorLine = i;
                _owner._cursorCol = idx - lineStart;
                _owner._statusMessage = $"[green]找到 '{searchTerm.EscapeMarkup()}' (行 {i + 1}, 列 {_owner._cursorCol + 1})[/]";
                return;
            }
            lineStart += _owner._inputLines[i].Length + 1;
        }
    }

    private Task<bool> HandleArrowUp(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (_owner._cursorLine > 0)
        {
            _owner._cursorLine--;
            _owner._cursorCol = Math.Min(_owner._cursorCol, _owner._inputLines[_owner._cursorLine].Length);
            _owner.InvalidateRendered();
        }
        return Task.FromResult(true);
    }

    private Task<bool> HandleArrowDown(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (_owner._cursorLine < _owner._inputLines.Count - 1)
        {
            _owner._cursorLine++;
            _owner._cursorCol = Math.Min(_owner._cursorCol, _owner._inputLines[_owner._cursorLine].Length);
            _owner.InvalidateRendered();
        }
        return Task.FromResult(true);
    }

    private Task<bool> HandlePageUp(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (_owner._scrollOffset < _owner._history.Count - 1)
            _owner._scrollOffset = Math.Min(_owner._scrollOffset + 3, Math.Max(0, _owner._history.Count - 1));
        _owner.InvalidateRendered();
        return Task.FromResult(true);
    }

    private Task<bool> HandlePageDown(ConsoleKeyInfo key, CancellationToken ct)
    {
        _owner._scrollOffset = Math.Max(0, _owner._scrollOffset - 3);
        _owner.InvalidateRendered();
        return Task.FromResult(true);
    }

    private async Task<bool> HandleTab(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (_owner.IsInputEmpty())
        {
            await _owner.CycleAgentModeAsync().ConfigureAwait(false);
        }
        else
        {
            for (var i = 0; i < 4; i++)
                _owner.InsertChar(' ');
        }
        return true;
    }

    private async Task<bool> HandleEnter(ConsoleKeyInfo key, CancellationToken ct)
    {
        // Shift+Enter → newline
        if (Mods(key, ConsoleModifiers.Shift))
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

        // Plain Enter → send
        var input = _owner.GetInputText().Trim();
        _owner.ClearInput();
        _owner._scrollOffset = 0;
        if (string.IsNullOrEmpty(input)) return true;

        _owner.SaveToHistory(input);
        _owner._historyIndex = -1;

        if (input.StartsWith('/'))
        {
            // Queue to main loop — avoids Spectre.Console concurrent guard
            // (LiveDisplayContext is on main thread; slash commands may use AnsiConsole)
            await _owner._messageQueue.Writer.WriteAsync("/!" + input, ct).ConfigureAwait(false);
            return true;
        }

        lock (_owner._history) _owner._history.Add(("user", null, input, null));
        _owner.TrimHistory();
        await _owner._messageQueue.Writer.WriteAsync(input, ct).ConfigureAwait(false);
        return true;
    }

    private Task<bool> HandleBackspace(ConsoleKeyInfo key, CancellationToken ct)
    {
        _owner.BackspaceChar();
        if (_owner._pickerActive && _owner._inputLines.Count == 1 && _owner._inputLines[0].Length <= 1)
            _owner._pickerActive = false;
        return Task.FromResult(true);
    }

    private Task<bool> HandleDelete(ConsoleKeyInfo key, CancellationToken ct)
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
        _owner.InvalidateRendered();
        return Task.FromResult(true);
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
                {
                    if (_owner._pickerSelectedIdx > 0)
                        _owner._pickerSelectedIdx--;
                    else
                    {
                        var ws = _owner.CalculatePickerWindowSize();
                        _owner._pickerSelectedIdx = _owner._pickerItems.Count - 1;
                        _owner._pickerScrollOffset = Math.Max(0, _owner._pickerItems.Count - ws);
                    }
                    if (_owner._pickerSelectedIdx < _owner._pickerScrollOffset)
                        _owner._pickerScrollOffset = _owner._pickerSelectedIdx;
                }
            }
            else if (key.Key == ConsoleKey.DownArrow || (key.Key == ConsoleKey.J && _owner._pickerFilter.Length == 0))
            {
                if (_owner._pickerItems.Count > 0)
                {
                    if (_owner._pickerSelectedIdx < _owner._pickerItems.Count - 1)
                        _owner._pickerSelectedIdx++;
                    else
                    {
                        _owner._pickerSelectedIdx = 0;
                        _owner._pickerScrollOffset = 0;
                    }
                    var ws = _owner.CalculatePickerWindowSize();
                    if (_owner._pickerSelectedIdx >= _owner._pickerScrollOffset + ws)
                        _owner._pickerScrollOffset = _owner._pickerSelectedIdx - ws + 1;
                }
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
            var filter = _owner._pickerFilter;

            lock (_owner._pickerLock)
            {
                _owner._pickerActive = false;
                State = InputState.Normal;
                _owner._pickerFilter = "";
                _owner._pickerItems = new();
                _owner._pickerSelectedIdx = -1;
                _owner._pickerScrollOffset = 0;
            }

            _owner.ClearInput();

            if (pickerResult != null)
            {
                await _owner._messageQueue.Writer.WriteAsync("/!" + pickerResult, ct).ConfigureAwait(false);
            }
            else if (!string.IsNullOrEmpty(filter))
            {
                await _owner._messageQueue.Writer.WriteAsync("/!/" + filter, ct).ConfigureAwait(false);
            }
        }

        return true;
    }

    // ═══════════════════════════════════════════════
    //  Model picker
    // ═══════════════════════════════════════════════

    private bool HandleModelPickerKey(ConsoleKeyInfo key, CancellationToken ct)
    {
        var isInputMode = _owner._modelPickerItems.Count == 0
            && !string.IsNullOrEmpty(_owner._modelPickerApiKeyEnvVar);

        if (isInputMode)
        {
            if (key.Key == ConsoleKey.Enter)
            {
                var keyText = _owner._modelPickerInputBuffer.Trim();
                if (keyText.Length > 0)
                {
                    SecretManager.Set(_owner._modelPickerApiKeyEnvVar, keyText);
                    _owner._modelPickerActive = false;
                    _owner._modelPickerApiKeyEnvVar = "";
                    _owner._modelPickerInputBuffer = "";
                    var cmd = $"/model {_owner._modelPickerLayer} {_owner._modelPickerProvider}";
                    _owner._messageQueue.Writer.TryWrite("/!" + cmd);
                }
                _owner.InvalidateRendered();
                return true;
            }
            if (key.Key == ConsoleKey.Escape)
            {
                _owner._modelPickerActive = false;
                _owner._modelPickerApiKeyEnvVar = "";
                _owner._modelPickerInputBuffer = "";
                _owner.InvalidateRendered();
                return true;
            }
            if (key.Key == ConsoleKey.Backspace && _owner._modelPickerInputBuffer.Length > 0)
            {
                _owner._modelPickerInputBuffer = _owner._modelPickerInputBuffer[..^1];
                _owner.InvalidateRendered();
                return true;
            }
            if (!char.IsControl(key.KeyChar))
            {
                _owner._modelPickerInputBuffer += key.KeyChar;
                _owner.InvalidateRendered();
                return true;
            }
            return true;
        }

        if (key.Key == ConsoleKey.UpArrow)
        {
            if (_owner._modelPickerSelectedIdx > 0)
                _owner._modelPickerSelectedIdx--;
            _owner.InvalidateRendered();
            return true;
        }
        if (key.Key == ConsoleKey.DownArrow)
        {
            if (_owner._modelPickerSelectedIdx < _owner._modelPickerItems.Count - 1)
                _owner._modelPickerSelectedIdx++;
            _owner.InvalidateRendered();
            return true;
        }
        if (key.Key == ConsoleKey.Enter)
        {
            var idx = _owner._modelPickerSelectedIdx;
            if (idx >= 0 && idx < _owner._modelPickerItems.Count)
            {
                var model = _owner._modelPickerItems[idx];
                var cmd = $"/model {_owner._modelPickerLayer} {_owner._modelPickerProvider} {model}";
                _owner._modelPickerActive = false;
                _owner._messageQueue.Writer.TryWrite("/!" + cmd);
                _owner.InvalidateRendered();
            }
            return true;
        }
        if (key.Key == ConsoleKey.Escape)
        {
            _owner._modelPickerActive = false;
            _owner.InvalidateRendered();
            return true;
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
            _owner._cascadeActive = false;
            _owner.InvalidateRendered();
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
                _owner._messageQueue.Writer.TryWrite("/!" + p);
            }
            else if (SlashCommands.PendingTextPrompt != null)
            {
                _owner._textInputPrompt = SlashCommands.PendingTextPrompt!;
                _owner._textInputIsSecret = SlashCommands.PendingTextIsSecret;
                _owner._textInputPrefix = SlashCommands.PendingTextPrefix ?? "";
                _owner._textInputBuffer = "";
                _owner._textInputActive = true;
                SlashCommands.PendingTextPrompt = null;
                SlashCommands.PendingTextIsSecret = false;
                SlashCommands.PendingTextPrefix = null;
            }
            _owner._cascadeActive = false;
            _owner.InvalidateRendered();
            _owner.InvalidateRendered();
            return true;
        }

        // Cascade display updated — trigger re-render
        _owner.InvalidateRendered();
        _owner.InvalidateRendered();
        return true;
    }

    // ═══════════════════════════════════════════════
    //  Text input overlay (replaces AnsiConsole.Prompt)
    // ═══════════════════════════════════════════════

    private bool HandleTextInputKey(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (key.Key == ConsoleKey.Enter)
        {
            var input = _owner._textInputBuffer.Trim();
            var tcs = _owner._textInputTcs;
            if (tcs != null)
            {
                // TCS mode (UserInputService.PromptAsync)
                tcs.TrySetResult(input);
                _owner._textInputTcs = null;
            }
            else
            {
                // Cascade mode — queue as slash command
                if (input.Length > 0)
                {
                    var cmd = _owner._textInputPrefix + " " + input;
                    _owner._messageQueue.Writer.TryWrite("/!" + cmd);
                }
            }
            _owner._textInputActive = false;
            _owner._textInputBuffer = "";
            _owner._textInputPrefix = "";
            _owner._textInputPrompt = "";
            _owner._textInputTcs = null;
            _owner.InvalidateRendered();
            return true;
        }
        if (key.Key == ConsoleKey.Escape)
        {
            var tcs = _owner._textInputTcs;
            if (tcs != null)
                tcs.TrySetResult(null);
            _owner._textInputActive = false;
            _owner._textInputBuffer = "";
            _owner._textInputPrefix = "";
            _owner._textInputPrompt = "";
            _owner._textInputTcs = null;
            _owner.InvalidateRendered();
            return true;
        }
        if (key.Key == ConsoleKey.Backspace && _owner._textInputBuffer.Length > 0)
        {
            _owner._textInputBuffer = _owner._textInputBuffer[..^1];
            _owner.InvalidateRendered();
            return true;
        }
        if (!char.IsControl(key.KeyChar))
        {
            _owner._textInputBuffer += key.KeyChar;
            _owner.InvalidateRendered();
            return true;
        }
        return true;
    }

    // ═══════════════════════════════════════════════
    //  Question overlay (replaces AnsiConsole.Prompt)
    // ═══════════════════════════════════════════════

    private bool HandleQuestionKey(ConsoleKeyInfo key, CancellationToken ct)
    {
        var q = _owner._currentQuestionPrompt;
        if (q == null) { _owner._questionActive = false; _owner.InvalidateRendered(); return true; }

        if (q.Options.Count > 0)
        {
            if (q.Multiple)
                return HandleQuestionMultiChoice(key, q);
            else
                return HandleQuestionSingleChoice(key, q);
        }

        // Free-text question
        if (key.Key == ConsoleKey.Enter)
        {
            CompleteQuestion(new[] { _owner._questionInput.Trim() });
            return true;
        }
        if (key.Key == ConsoleKey.Escape)
        {
            CompleteQuestion(new[] { "(跳过)" });
            return true;
        }
        if (key.Key == ConsoleKey.Backspace && _owner._questionInput.Length > 0)
        {
            _owner._questionInput = _owner._questionInput[..^1];
            _owner.InvalidateRendered();
            return true;
        }
        if (!char.IsControl(key.KeyChar))
        {
            _owner._questionInput += key.KeyChar;
            _owner.InvalidateRendered();
            return true;
        }
        return true;
    }

    private bool HandleQuestionSingleChoice(ConsoleKeyInfo key, QuestionPrompt q)
    {
        var letter = (char)('a' + Array.IndexOf(q.Options.ToArray(), q.Options.FirstOrDefault()));
        var idx = key.KeyChar >= 'a' && key.KeyChar <= 'z'
            ? key.KeyChar - 'a'
            : -1;

        if (idx >= 0 && idx < q.Options.Count)
        {
            CompleteQuestion(new[] { q.Options[idx].Label });
            return true;
        }

        if (key.KeyChar == 'c')
        {
            // Switch to custom text input
            PromptCustomAnswer(q);
            return true;
        }

        if (key.Key == ConsoleKey.Enter && _owner._questionInput.Length > 0)
        {
            CompleteQuestion(new[] { _owner._questionInput.Trim() });
            return true;
        }

        if (key.Key == ConsoleKey.Escape)
        {
            CompleteQuestion(new[] { "(跳过)" });
            return true;
        }

        return true;
    }

    private bool HandleQuestionMultiChoice(ConsoleKeyInfo key, QuestionPrompt q)
    {
        if (key.Key == ConsoleKey.Enter)
        {
            CompleteQuestion(_owner._questionMultiSelection.Count > 0
                ? _owner._questionMultiSelection.ToArray()
                : new[] { "(跳过)" });
            return true;
        }

        if (key.KeyChar == 'c')
        {
            PromptCustomAnswer(q);
            return true;
        }

        if (key.KeyChar >= '1' && key.KeyChar <= '9')
        {
            var idx = key.KeyChar - '1';
            if (idx < q.Options.Count)
            {
                var label = q.Options[idx].Label;
                if (_owner._questionMultiSelection.Contains(label))
                    _owner._questionMultiSelection.Remove(label);
                else
                    _owner._questionMultiSelection.Add(label);
                _owner.InvalidateRendered();
            }
            return true;
        }

        if (key.Key == ConsoleKey.Escape)
        {
            CompleteQuestion(new[] { "(跳过)" });
            return true;
        }

        return true;
    }

    private void CompleteQuestion(IReadOnlyList<string> answer)
    {
        var tcs = _owner._questionTcs;
        _owner._questionTcs = null;
        _owner._questionActive = false;
        _owner._questionInput = "";
        _owner._questionMultiSelection.Clear();
        _owner.InvalidateRendered();
        tcs?.TrySetResult(answer);
    }

    private void PromptCustomAnswer(QuestionPrompt q)
    {
        // Delegate to text input overlay
        var tcs = new TaskCompletionSource<string?>();
        _owner._textInputPrompt = "[yellow]输入自定义回答:[/]";
        _owner._textInputIsSecret = false;
        _owner._textInputBuffer = "";
        _owner._textInputTcs = tcs;
        _owner._textInputActive = true;
        _owner.InvalidateRendered();

        // When text input completes, use result as answer
        _ = tcs.Task.ContinueWith(t =>
        {
            var result = t.Result;
            CompleteQuestion(result != null ? new[] { result } : new[] { "(跳过)" });
        }, TaskScheduler.Default);
    }

    // ═══════════════════════════════════════════════
    //  View switcher
    // ═══════════════════════════════════════════════

    private bool HandleViewPicker(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            _owner._viewPickerActive = false;
            return true;
        }

        if (key.Key == ConsoleKey.UpArrow)
        {
            _owner._viewPickerSelected = Math.Max(0, _owner._viewPickerSelected - 1);
            return true;
        }

        if (key.Key == ConsoleKey.DownArrow)
        {
            _owner._viewPickerSelected = Math.Min(ChatLayout.ViewOptions.Length - 1, _owner._viewPickerSelected + 1);
            return true;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            var idx = Math.Clamp(_owner._viewPickerSelected, 0, ChatLayout.ViewOptions.Length - 1);
            _owner._quickNav = ChatLayout.ViewOptions[idx].key[0];
            _owner._viewPickerActive = false;
            return true;
        }

        // Number keys jump directly
        if ("0123456789".Contains(key.KeyChar))
        {
            _owner._quickNav = key.KeyChar;
            _owner._viewPickerActive = false;
            return true;
        }

        return true;
    }

    // ═══════════════════════════════════════════════
    //  Utilities
    // ═══════════════════════════════════════════════

    private static bool Mods(ConsoleKeyInfo key, ConsoleModifiers mod) =>
        (key.Modifiers & mod) != 0;

}
