using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI.Rendering;

internal sealed class FramebufferRenderer : IDisposable
{
    private Cell[,] _prev;
    private Cell[,] _curr;
    private int _width;
    private int _height;

    internal struct Cell : IEquatable<Cell>
    {
        public char Char;
        public Color? Foreground;
        public Color? Background;

        public bool Equals(Cell other) =>
            Char == other.Char &&
            Nullable.Equals(Foreground, other.Foreground) &&
            Nullable.Equals(Background, other.Background);

        public override bool Equals(object? obj) => obj is Cell other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Char, Foreground, Background);
    }

    public FramebufferRenderer()
    {
        _width = Math.Max(1, SafeWindowWidth);
        _height = Math.Max(1, SafeWindowHeight);
        _prev = new Cell[_height, _width];
        _curr = new Cell[_height, _width];
    }

    private static int SafeWindowWidth
    {
        get { try { return Console.WindowWidth; } catch { return 120; } }
    }

    private static int SafeWindowHeight
    {
        get { try { return Console.WindowHeight; } catch { return 40; } }
    }

    public void Initialize()
    {
        Console.CursorVisible = false;
        Console.Out.Write("\u001b[2J\u001b[H");
        Console.Out.Flush();
    }

    public void RenderAndFlush(IRenderable renderable)
    {
        int w = SafeWindowWidth;
        int h = SafeWindowHeight;
        if (w < 1) w = 1;
        if (h < 1) h = 1;
        if (w != _width || h != _height)
        {
            _width = w;
            _height = h;
            _prev = new Cell[_height, _width];
            _curr = new Cell[_height, _width];
        }

        Array.Clear(_curr, 0, _curr.Length);

        var capabilities = AnsiConsole.Console.Profile.Capabilities;
        var termSize = new Size(_width, _height);
        var options = new RenderOptions(capabilities, termSize);
        var segments = renderable.Render(options, _width);

        WriteSegments(segments);
        FlushDiff();

        var temp = _prev;
        _prev = _curr;
        _curr = temp;
    }

    private void WriteSegments(IEnumerable<Segment> segments)
    {
        var rows = new List<List<Cell>>() { new() };
        foreach (var seg in segments)
        {
            if (seg.IsLineBreak)
            {
                rows.Add([]);
                continue;
            }
            if (seg.IsControlCode) continue;

            var fg = seg.Style.Foreground;
            var bg = seg.Style.Background;
            foreach (char c in seg.Text)
            {
                var row = rows[^1];
                if (row.Count >= _width)
                {
                    rows.Add([]);
                    row = rows[^1];
                }
                row.Add(new Cell { Char = c, Foreground = fg, Background = bg });
            }
        }

        int count = Math.Min(rows.Count, _height);
        int srcStart = rows.Count - count;

        for (int r = 0; r < count; r++)
        {
            int srcR = srcStart + r;
            int numCols = Math.Min(rows[srcR].Count, _width);
            for (int c = 0; c < _width; c++)
                _curr[r, c] = c < numCols ? rows[srcR][c] : default;
        }
        for (int r = count; r < _height; r++)
            Array.Clear(_curr, r * _width, _width);
    }

    private void FlushDiff()
    {
        var sb = new StringBuilder();

        for (int r = 0; r < _height; r++)
        {
            int segStart = -1;
            for (int c = 0; c <= _width; c++)
            {
                bool changed = c < _width && !_curr[r, c].Equals(_prev[r, c]);
                if (changed && segStart < 0)
                    segStart = c;
                if (!changed && segStart >= 0)
                {
                    DumpSegment(sb, r, segStart, c);
                    segStart = -1;
                }
            }
        }

        if (sb.Length > 0)
        {
            sb.Append("\u001b[0m");
            Console.Out.Write(sb.ToString());
            Console.Out.Flush();
        }
    }

    private void DumpSegment(StringBuilder sb, int row, int from, int to)
    {
        int i = from;
        while (i < to)
        {
            var cell = _curr[row, i];
            int j = i + 1;
            while (j < to && SameStyle(_curr[row, j], cell))
                j++;

            sb.Append($"\u001b[{row + 1};{i + 1}H");
            AppendStyle(sb, cell.Foreground, cell.Background);
            for (int k = i; k < j; k++)
                sb.Append(_curr[row, k].Char);
            i = j;
        }
    }

    private static bool SameStyle(Cell a, Cell b) =>
        Nullable.Equals(a.Foreground, b.Foreground) &&
        Nullable.Equals(a.Background, b.Background);

    private static void AppendStyle(StringBuilder sb, Color? fg, Color? bg)
    {
        if (fg.HasValue)
            sb.Append($"\u001b[38;2;{fg.Value.R};{fg.Value.G};{fg.Value.B}m");
        if (bg.HasValue)
            sb.Append($"\u001b[48;2;{bg.Value.R};{bg.Value.G};{bg.Value.B}m");
    }

    public void Shutdown()
    {
        Console.Out.Write("\u001b[0m\u001b[2J\u001b[H");
        Console.Out.Flush();
        Console.CursorVisible = true;
    }

    public void Dispose() => Shutdown();
}
