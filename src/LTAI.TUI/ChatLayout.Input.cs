namespace LTAI.TUI;

partial class ChatLayout
{
    // ── 多行输入帮助方法 ──

    internal bool IsInputEmpty() => _inputLines.Count == 1 && _inputLines[0].Length == 0;

    internal string GetInputText() => string.Join("\n", _inputLines);

    internal void ClearInput()
    {
        _inputLines.Clear();
        _inputLines.Add("");
        _cursorLine = 0;
        _cursorCol = 0;
        InvalidateRendered();
    }

    internal void SetInput(string text)
    {
        ClearInput();
        var lines = text.Split('\n');
        _inputLines.Clear();
        _inputLines.AddRange(lines);
        _cursorLine = _inputLines.Count - 1;
        _cursorCol = _inputLines[^1].Length;
        InvalidateRendered();
    }

    internal void InsertChar(char c)
    {
        var line = _inputLines[_cursorLine];
        _inputLines[_cursorLine] = line.Insert(_cursorCol, c.ToString());
        _cursorCol++;
        InvalidateRendered();
    }

    internal void BackspaceChar()
    {
        if (_cursorCol > 0)
        {
            var line = _inputLines[_cursorLine];
            _inputLines[_cursorLine] = line.Remove(_cursorCol - 1, 1);
            _cursorCol--;
        }
        else if (_cursorLine > 0)
        {
            var prevLine = _inputLines[_cursorLine - 1];
            _cursorCol = prevLine.Length;
            _inputLines[_cursorLine - 1] = prevLine + _inputLines[_cursorLine];
            _inputLines.RemoveAt(_cursorLine);
            _cursorLine--;
        }
        InvalidateRendered();
    }

    internal void ReplaceInputLine(string text)
    {
        _inputLines.Clear();
        _inputLines.Add(text);
        _cursorLine = 0;
        _cursorCol = text.Length;
        InvalidateRendered();
    }

    internal void SaveToHistory(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return;
        if (_inputHistory.Count > 0 && _inputHistory[^1] == input) return;
        _inputHistory.Add(input);
        if (_inputHistory.Count > 50) _inputHistory.RemoveAt(0);
        _historyIndex = -1;
    }

    internal bool CheckPickerTrigger()
    {
        if (_inputLines.Count == 1 && _inputLines[0] == "/")
        {
            lock (_pickerLock)
            {
                _pickerActive = true;
                _pickerFilter = "";
                _pickerItems = SlashCommands.GetSuggestionItems("/");
                _pickerSelectedIdx = _pickerItems.Count > 0 ? 0 : -1;
                _pickerScrollOffset = 0;
            }
            return true;
        }
        return false;
    }

    internal void UpdatePickerItems()
    {
        var prefix = "/" + _pickerFilter;
        _pickerItems = prefix.Length > 1
            ? SlashCommands.GetSuggestionItems(prefix)
            : SlashCommands.GetSuggestionItems("/");
        if (_pickerSelectedIdx >= _pickerItems.Count) _pickerSelectedIdx = _pickerItems.Count - 1;
        if (_pickerSelectedIdx < 0 && _pickerItems.Count > 0) _pickerSelectedIdx = 0;
        _pickerScrollOffset = 0;
    }
}
