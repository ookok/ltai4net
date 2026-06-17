using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI.Rendering;

internal sealed class FramebufferRenderer : IDisposable
{
    private const int MaxWidth = 300;
    private const int MaxHeight = 200;

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
        _prev = new Cell[MaxHeight, MaxWidth];
        _curr = new Cell[MaxHeight, MaxWidth];
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
        try { Console.CursorVisible = false; } catch { /* non-critical in headless/test env */ }
        Console.Out.Write("\u001b[2J\u001b[H");
        Console.Out.Flush();
    }

    public void RenderAndFlush(IRenderable renderable)
    {
        int w = SafeWindowWidth;
        int h = SafeWindowHeight;
        if (w < 1) w = 1;
        if (h < 1) h = 1;
        if (w > MaxWidth) w = MaxWidth;
        if (h > MaxHeight) h = MaxHeight;
        _width = w;
        _height = h;

        Array.Clear(_curr, 0, _curr.Length);

        var capabilities = AnsiConsole.Console.Profile.Capabilities;
        var options = new RenderOptions(capabilities, new Size(_width, _height));
        var segments = renderable.Render(options, _width);

        WriteSegments(segments);
        FlushDiff();

        var temp = _prev;
        _prev = _curr;
        _curr = temp;
    }

    private void WriteSegments(IEnumerable<Segment> segments)
    {
        int row = 0, col = 0;
        foreach (var seg in segments)
        {
            if (seg.IsLineBreak)
            {
                row++;
                col = 0;
                if (row >= _height) break;
                continue;
            }
            if (seg.IsControlCode) continue;

            var fg = seg.Style.Foreground;
            var bg = seg.Style.Background;
            foreach (char c in seg.Text)
            {
                if (col >= _width)
                {
                    row++;
                    col = 0;
                    if (row >= _height) break;
                }
                _curr[row, col] = new Cell { Char = c, Foreground = fg, Background = bg };
                col++;
            }
        }
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

            sb.Append("\u001b[");
            sb.Append(row + 1);
            sb.Append(';');
            sb.Append(i + 1);
            sb.Append('H');
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
        {
            sb.Append("\u001b[38;2;");
            sb.Append(fg.Value.R);
            sb.Append(';');
            sb.Append(fg.Value.G);
            sb.Append(';');
            sb.Append(fg.Value.B);
            sb.Append('m');
        }
        if (bg.HasValue)
        {
            sb.Append("\u001b[48;2;");
            sb.Append(bg.Value.R);
            sb.Append(';');
            sb.Append(bg.Value.G);
            sb.Append(';');
            sb.Append(bg.Value.B);
            sb.Append('m');
        }
    }

    public void Shutdown()
    {
        Console.Out.Write("\u001b[0m\u001b[2J\u001b[H");
        Console.Out.Flush();
        try { Console.CursorVisible = true; } catch { /* non-critical in headless/test env */ }
    }

    public void Dispose() => Shutdown();
}
