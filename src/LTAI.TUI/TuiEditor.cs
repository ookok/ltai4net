using System.Text;
using Spectre.Console;

namespace LTAI.TUI;

public sealed class TuiEditor
{
    private readonly List<StringBuilder> _lines = new();
    private int _cursorRow, _cursorCol;
    private int _scrollRow;
    private string? _filePath;
    private bool _modified;
    private string? _searchTerm;
    private List<(int row, int col)> _searchMatches = new();
    private int _currentMatchIdx = -1;

    public async Task<string?> EditAsync(string? filePath = null, string? content = null)
    {
        _filePath = filePath;
        _modified = false;
        _cursorRow = 0;
        _cursorCol = 0;
        _scrollRow = 0;
        _searchTerm = null;
        _searchMatches.Clear();
        _currentMatchIdx = -1;

        _lines.Clear();
        if (content != null)
        {
            foreach (var line in content.Split('\n'))
                _lines.Add(new StringBuilder(line.TrimEnd('\r')));
        }
        else if (filePath != null && File.Exists(filePath))
        {
            foreach (var line in await File.ReadAllLinesAsync(filePath))
                _lines.Add(new StringBuilder(line));
        }

        if (_lines.Count == 0)
            _lines.Add(new StringBuilder());

        Console.CursorVisible = true;
        var originalTitle = Console.Title;
        try
        {
            return await EditLoopAsync();
        }
        finally
        {
            Console.CursorVisible = false;
            Console.Title = originalTitle;
        }
    }

    private async Task<string?> EditLoopAsync()
    {
        var findMode = false;
        var findBuffer = new StringBuilder();

        while (true)
        {
            Render(findMode, findBuffer);
            var key = Console.ReadKey(true);

            if (findMode)
            {
                if (key.Key == ConsoleKey.Escape)
                {
                    findMode = false;
                    findBuffer.Clear();
                    _searchTerm = null;
                }
                else if (key.Key == ConsoleKey.Enter)
                {
                    findMode = false;
                    _searchTerm = findBuffer.ToString();
                }
                else if (key.Key == ConsoleKey.Backspace && findBuffer.Length > 0)
                {
                    findBuffer.Remove(findBuffer.Length - 1, 1);
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    findBuffer.Append(key.KeyChar);
                }
                continue;
            }

            if (key.Modifiers == ConsoleModifiers.Control)
            {
                switch (key.Key)
                {
                    case ConsoleKey.S:
                        await SaveAsync();
                        return GetContent();
                    case ConsoleKey.F:
                        findMode = true;
                        findBuffer.Clear();
                        continue;
                    case ConsoleKey.Home:
                        _cursorRow = 0;
                        _cursorCol = 0;
                        break;
                    case ConsoleKey.End:
                        _cursorRow = _lines.Count - 1;
                        _cursorCol = _lines[_cursorRow].Length;
                        break;
                    case ConsoleKey.A:
                        _cursorCol = 0;
                        break;
                    case ConsoleKey.E:
                        _cursorCol = _lines[_cursorRow].Length;
                        break;
                    case ConsoleKey.K:
                        if (_cursorCol < _lines[_cursorRow].Length)
                        {
                            _lines[_cursorRow].Remove(_cursorCol, _lines[_cursorRow].Length - _cursorCol);
                            _modified = true;
                        }
                        else if (_cursorRow < _lines.Count - 1)
                        {
                            var rest = _lines[_cursorRow + 1].ToString();
                            _lines.RemoveAt(_cursorRow + 1);
                            _lines[_cursorRow].Append(rest);
                            _modified = true;
                        }
                        break;
                }
                continue;
            }

            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    if (_searchTerm != null)
                    {
                        _searchTerm = null;
                        _searchMatches.Clear();
                        _currentMatchIdx = -1;
                        break;
                    }
                    else if (_modified)
                    {
                        return null;
                    }
                    else
                    {
                        return GetContent();
                    }

                case ConsoleKey.UpArrow:
                    if (_cursorRow > 0)
                        _cursorRow--;
                    _cursorCol = Math.Min(_cursorCol, _lines[_cursorRow].Length);
                    break;

                case ConsoleKey.DownArrow:
                    if (_cursorRow < _lines.Count - 1)
                        _cursorRow++;
                    _cursorCol = Math.Min(_cursorCol, _lines[_cursorRow].Length);
                    break;

                case ConsoleKey.LeftArrow:
                    if (_cursorCol > 0)
                        _cursorCol--;
                    else if (_cursorRow > 0)
                    {
                        _cursorRow--;
                        _cursorCol = _lines[_cursorRow].Length;
                    }
                    break;

                case ConsoleKey.RightArrow:
                    if (_cursorCol < _lines[_cursorRow].Length)
                        _cursorCol++;
                    else if (_cursorRow < _lines.Count - 1)
                    {
                        _cursorRow++;
                        _cursorCol = 0;
                    }
                    break;

                case ConsoleKey.PageUp:
                    _cursorRow = Math.Max(0, _cursorRow - (Console.WindowHeight - 4));
                    _cursorCol = Math.Min(_cursorCol, _lines[_cursorRow].Length);
                    break;

                case ConsoleKey.PageDown:
                    _cursorRow = Math.Min(_lines.Count - 1, _cursorRow + (Console.WindowHeight - 4));
                    _cursorCol = Math.Min(_cursorCol, _lines[_cursorRow].Length);
                    break;

                case ConsoleKey.Home:
                    _cursorCol = 0;
                    break;

                case ConsoleKey.End:
                    _cursorCol = _lines[_cursorRow].Length;
                    break;

                case ConsoleKey.Enter:
                    var newLine = new StringBuilder(_lines[_cursorRow].ToString()[_cursorCol..]);
                    _lines[_cursorRow].Remove(_cursorCol, _lines[_cursorRow].Length - _cursorCol);
                    _lines.Insert(_cursorRow + 1, newLine);
                    _cursorRow++;
                    _cursorCol = 0;
                    _modified = true;
                    break;

                case ConsoleKey.Backspace:
                    if (_cursorCol > 0)
                    {
                        _lines[_cursorRow].Remove(_cursorCol - 1, 1);
                        _cursorCol--;
                        _modified = true;
                    }
                    else if (_cursorRow > 0)
                    {
                        _cursorCol = _lines[_cursorRow - 1].Length;
                        _lines[_cursorRow - 1].Append(_lines[_cursorRow]);
                        _lines.RemoveAt(_cursorRow);
                        _cursorRow--;
                        _modified = true;
                    }
                    break;

                case ConsoleKey.Delete:
                    if (_cursorCol < _lines[_cursorRow].Length)
                    {
                        _lines[_cursorRow].Remove(_cursorCol, 1);
                        _modified = true;
                    }
                    else if (_cursorRow < _lines.Count - 1)
                    {
                        _lines[_cursorRow].Append(_lines[_cursorRow + 1]);
                        _lines.RemoveAt(_cursorRow + 1);
                        _modified = true;
                    }
                    break;

                case ConsoleKey.Tab:
                    _lines[_cursorRow].Insert(_cursorCol, "    ");
                    _cursorCol += 4;
                    _modified = true;
                    break;

                case ConsoleKey.F3:
                    if (!string.IsNullOrEmpty(_searchTerm))
                        FindNext(_searchTerm);
                    break;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        _lines[_cursorRow].Insert(_cursorCol, key.KeyChar);
                        _cursorCol++;
                        _modified = true;
                    }
                    break;
            }

            EnsureCursorVisible();
        }
    }

    private void EnsureCursorVisible()
    {
        var editorHeight = Console.WindowHeight - 3;
        if (editorHeight <= 0) return;

        if (_cursorRow < _scrollRow)
            _scrollRow = _cursorRow;
        else if (_cursorRow >= _scrollRow + editorHeight)
            _scrollRow = _cursorRow - editorHeight + 1;

        _scrollRow = Math.Max(0, Math.Min(_scrollRow, _lines.Count - 1));
    }

    private void FindNext(string term)
    {
        var startRow = _currentMatchIdx >= 0 && _currentMatchIdx < _searchMatches.Count
            ? _searchMatches[_currentMatchIdx].row
            : _cursorRow;

        var found = false;
        for (int r = startRow; r < _lines.Count && !found; r++)
        {
            var startCol = r == startRow ? _cursorCol + 1 : 0;
            var idx = _lines[r].ToString().IndexOf(term, startCol, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                _cursorRow = r;
                _cursorCol = idx;
                EnsureCursorVisible();
                found = true;
            }
        }

        if (!found)
        {
            for (int r = 0; r <= startRow && !found; r++)
            {
                var idx = _lines[r].ToString().IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    _cursorRow = r;
                    _cursorCol = idx;
                    EnsureCursorVisible();
                    found = true;
                }
            }
        }
    }

    private void Render(bool findMode, StringBuilder findBuffer)
    {
        var editorHeight = Console.WindowHeight - 3;
        if (editorHeight <= 0) return;

        Console.SetCursorPosition(0, 0);
        Console.ResetColor();

        var fileName = _filePath != null ? Path.GetFileName(_filePath) : "untitled.md";
        var modified = _modified ? " [modified]" : "";
        var status = $"{fileName}{modified}  Ln {_cursorRow + 1}/{_lines.Count}, Col {_cursorCol + 1}";
        var padded = status.PadRight(Console.WindowWidth)[..Math.Min(status.Length, Console.WindowWidth)];
        Console.BackgroundColor = ConsoleColor.DarkBlue;
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(padded);
        Console.ResetColor();
        Console.WriteLine();

        for (int i = 0; i < editorHeight && (_scrollRow + i) < _lines.Count; i++)
        {
            var lineIdx = _scrollRow + i;
            var lineText = _lines[lineIdx].ToString();

            var lineNum = $" {lineIdx + 1} ".PadLeft(6)[..6];
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(lineNum);
            Console.ResetColor();

            var availableWidth = Console.WindowWidth - 7;
            var display = lineText;
            var scrollCol = 0;

            var cursorOnThisLine = lineIdx == _cursorRow;

            if (cursorOnThisLine && _cursorCol >= availableWidth)
            {
                scrollCol = _cursorCol - availableWidth + 1;
                display = lineText[scrollCol..Math.Min(lineText.Length, scrollCol + availableWidth)];
            }
            else if (display.Length > availableWidth)
            {
                display = display[..availableWidth];
            }

            var cursorVisual = cursorOnThisLine ? _cursorCol - scrollCol : -1;

            if (cursorVisual >= 0)
            {
                if (cursorVisual > 0)
                    Console.Write(display[..cursorVisual]);
                Console.BackgroundColor = ConsoleColor.Gray;
                Console.ForegroundColor = ConsoleColor.Black;
                Console.Write(cursorVisual < display.Length ? display[cursorVisual].ToString() : " ");
                Console.ResetColor();
                if (cursorVisual + 1 < display.Length)
                    Console.Write(display[(cursorVisual + 1)..]);
            }
            else
            {
                Console.Write(display);
            }

            Console.Write(new string(' ', Math.Max(0, Console.WindowWidth - 7 - display.Length)));
            Console.WriteLine();
        }

        for (int i = _lines.Count - _scrollRow; i < editorHeight; i++)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(" ~".PadRight(6));
            Console.ResetColor();
            Console.Write(new string(' ', Console.WindowWidth - 7));
            Console.WriteLine();
        }

        if (findMode)
        {
            Console.BackgroundColor = ConsoleColor.DarkYellow;
            Console.ForegroundColor = ConsoleColor.Black;
            var findText = $" Find: {findBuffer}";
            Console.Write(findText.PadRight(Console.WindowWidth));
            Console.ResetColor();
        }
        else
        {
            Console.BackgroundColor = ConsoleColor.DarkBlue;
            Console.ForegroundColor = ConsoleColor.Gray;
            var help = _searchTerm != null
                ? $" Ctrl+S Save | Esc Exit | Ctrl+F Find | F3 Next (\"{_searchTerm}\")"
                : " Ctrl+S Save | Esc Exit | Ctrl+F Find | F3 Next";
            Console.Write(help.PadRight(Console.WindowWidth));
            Console.ResetColor();
        }

        if (!findMode)
        {
            var visualRow = _cursorRow - _scrollRow + 1;
            var visualCol = 7 + (_cursorCol < (Console.WindowWidth - 7) ? _cursorCol : Console.WindowWidth - 8);
            Console.SetCursorPosition(Math.Min(visualCol, Console.WindowWidth - 1), Math.Min(visualRow, Console.WindowHeight - 1));
        }
    }

    private async Task SaveAsync()
    {
        if (_filePath == null) return;
        var dir = Path.GetDirectoryName(_filePath);
        if (dir != null) Directory.CreateDirectory(dir);

        var content = GetContent();
        await File.WriteAllTextAsync(_filePath, content);
        _modified = false;
    }

    private string GetContent()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _lines.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(_lines[i]);
        }
        return sb.ToString();
    }
}
