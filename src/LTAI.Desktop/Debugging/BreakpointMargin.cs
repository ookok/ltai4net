using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace LTAI.Desktop.Debugging;

public sealed class BreakpointMargin : AbstractMargin
{
    private readonly BreakpointManager _bpManager;
    private readonly Func<string?> _getCurrentFile;
    private int _pausedLine = -1;
    private static readonly IBrush BpBrush = new SolidColorBrush(Color.Parse("#e74c3c"));
    private static readonly IBrush BpDisabledBrush = new SolidColorBrush(Color.Parse("#7f8c8d"));
    private static readonly IBrush CurrentLineBrush = new SolidColorBrush(Color.Parse("#f1c40f"));
    private static readonly Pen BpPen = new(new SolidColorBrush(Color.Parse("#c0392b")), 1.5);

    public BreakpointMargin(BreakpointManager bpManager, Func<string?> getCurrentFile)
    {
        _bpManager = bpManager;
        _getCurrentFile = getCurrentFile;
        Width = 18;
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
    }

    public void SetPausedLine(int line)
    {
        _pausedLine = line;
        InvalidateVisual();
    }

    public void ClearPausedLine()
    {
        _pausedLine = -1;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (TextView == null) return;

        var pos = e.GetPosition(this);
        var line = GetLineFromPosition(pos.Y);
        if (line > 0)
        {
            var file = _getCurrentFile();
            if (file != null)
            {
                _bpManager.Toggle(file, line);
                InvalidateVisual();
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(18, availableSize.Height);

    public override void Render(DrawingContext ctx)
    {
        if (TextView == null) return;

        var file = _getCurrentFile();
        if (file == null) return;

        foreach (var vl in TextView.VisualLines)
        {
            var line = vl.FirstDocumentLine.LineNumber;
            var y = vl.VisualTop;
            var h = vl.Height;

            // Current paused line — yellow arrow
            if (line == _pausedLine)
            {
                var arrow = new StreamGeometry();
                using (var gc = arrow.Open())
                {
                    gc.BeginFigure(new Point(2, y + h / 2), true);
                    gc.LineTo(new Point(14, y + 4));
                    gc.LineTo(new Point(14, y + h - 4));
                    gc.EndFigure(true);
                }
                ctx.DrawGeometry(CurrentLineBrush, null, arrow);
                continue;
            }

            // Breakpoint — red dot
            if (_bpManager.HasBreakpoint(file, line))
            {
                var cx = Width / 2;
                var cy = y + h / 2;
                ctx.DrawEllipse(BpBrush, BpPen, new Point(cx, cy), 6, 6);
            }
        }
    }

    private int GetLineFromPosition(double y)
    {
        if (TextView == null) return -1;
        foreach (var vl in TextView.VisualLines)
        {
            if (y >= vl.VisualTop && y <= vl.VisualTop + vl.Height)
                return vl.FirstDocumentLine.LineNumber;
        }
        return -1;
    }
}
