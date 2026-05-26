using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LTAI.Desktop;

public sealed class TaskDagView : UserControl
{
    private readonly LTAIService _svc;
    private readonly Canvas _canvas;
    private readonly DispatcherTimer _timer;
    private readonly ScrollViewer _scrollViewer;
    private readonly TextBlock _statsText;

    private const double NodeWidth = 160;
    private const double NodeHeight = 40;
    private const double HorizontalSpacing = 60;
    private const double VerticalSpacing = 30;

    public TaskDagView(LTAIService svc)
    {
        _svc = svc;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new DockPanel { Margin = new(16) };

        var header = new TextBlock
        {
            Text = "Task DAG Monitor",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary)
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        _statsText = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontFamily = new("Consolas"),
            FontSize = 12,
            Margin = new(0, 4, 0, 4)
        };
        DockPanel.SetDock(_statsText, Dock.Top);
        root.Children.Add(_statsText);

        var sep = new Border { Height = 1, Background = LtaiTheme.Sbb(LtaiTheme.Border), Margin = new(0, 0, 0, 8) };
        DockPanel.SetDock(sep, Dock.Top);
        root.Children.Add(sep);

        var legend = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Margin = new(0, 0, 0, 8)
        };
        legend.Children.Add(LegendItem("Pending", Color.Parse("#484f58")));
        legend.Children.Add(LegendItem("Ready", Color.Parse("#a371f7")));
        legend.Children.Add(LegendItem("Running", Color.Parse("#d29922")));
        legend.Children.Add(LegendItem("Done", Color.Parse("#3fb950")));
        legend.Children.Add(LegendItem("Failed", Color.Parse("#f85149")));
        DockPanel.SetDock(legend, Dock.Top);
        root.Children.Add(legend);

        _canvas = new Canvas { Background = LtaiTheme.Sbb(LtaiTheme.BgPanel) };
        _scrollViewer = new ScrollViewer { Content = _canvas };
        root.Children.Add(_scrollViewer);

        Content = root;

        _timer = new DispatcherTimer(
            TimeSpan.FromSeconds(2),
            DispatcherPriority.Background,
            (_, _) => Refresh());
        _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        Refresh();
    }

    private static StackPanel LegendItem(string label, Color color)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        sp.Children.Add(new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center
        });
        sp.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        });
        return sp;
    }

    private void Refresh()
    {
        _canvas.Children.Clear();

        var coordinator = TryGetCoordinator();
        if (coordinator == null)
        {
            var placeholder = new TextBlock
            {
                Text = "No coordinator active.",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontFamily = new("Consolas"),
                FontSize = 14
            };
            Canvas.SetLeft(placeholder, 20);
            Canvas.SetTop(placeholder, 20);
            _canvas.Children.Add(placeholder);
            _statsText.Text = "Coordinator: offline";
            return;
        }

        var sessions = coordinator.ActiveSessions.Values.ToList();
        var taskQueue = TryGetTaskQueue();

        var tasks = new List<(string Id, string Label, string Status, List<string> Deps)>();

        if (taskQueue != null)
        {
            var allTasks = taskQueue.GetAll();
            foreach (var t in allTasks)
            {
                tasks.Add((t.Id, t.Goal.Length > 40 ? t.Goal[..40] : t.Goal,
                    t.Status.ToString(), t.DependsOn));
            }
        }

        if (tasks.Count == 0)
        {
            foreach (var s in sessions)
            {
                var label = $"{s.AgentName}: {(s.Goal.Length > 40 ? s.Goal[..40] : s.Goal)}";
                var status = s.CompletedAt.HasValue ? (s.Result?.StartsWith("Error") ?? false ? "Failed" : "Completed") : "Running";
                tasks.Add((s.SessionId, label, status, new List<string>()));
            }
        }

        if (tasks.Count == 0)
        {
            var placeholder = new TextBlock
            {
                Text = "No active tasks.\n\nRun a team or agent to see the DAG.",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontFamily = new("Consolas"),
                FontSize = 14
            };
            Canvas.SetLeft(placeholder, 20);
            Canvas.SetTop(placeholder, 20);
            _canvas.Children.Add(placeholder);
            _statsText.Text = "Tasks: 0 | Running: 0 | Done: 0 | Failed: 0";
            return;
        }

        var layers = BuildLayers(tasks);
        DrawDag(layers);

        var running = tasks.Count(t => t.Status == "Running");
        var done = tasks.Count(t => t.Status == "Completed");
        var failed = tasks.Count(t => t.Status == "Failed");
        _statsText.Text = $"Tasks: {tasks.Count} | Running: {running} | Done: {done} | Failed: {failed}";
    }

    private List<List<(string Id, string Label, string Status, List<string> Deps)>> BuildLayers(
        List<(string Id, string Label, string Status, List<string> Deps)> tasks)
    {
        var layers = new List<List<(string, string, string, List<string>)>>();
        var placed = new HashSet<string>();
        var taskDict = tasks.ToDictionary(t => t.Id);

        while (placed.Count < tasks.Count)
        {
            var layer = new List<(string, string, string, List<string>)>();
            foreach (var t in tasks)
            {
                if (placed.Contains(t.Id)) continue;
                if (t.Deps.All(d => placed.Contains(d) || !taskDict.ContainsKey(d)))
                {
                    layer.Add(t);
                }
            }

            if (layer.Count == 0)
            {
                foreach (var t in tasks)
                {
                    if (!placed.Contains(t.Id))
                    {
                        layer.Add(t);
                        break;
                    }
                }
            }

            foreach (var item in layer) placed.Add(item.Item1);
            if (layer.Count > 0) layers.Add(layer);
            else break;
        }

        return layers;
    }

    private void DrawDag(List<List<(string Id, string Label, string Status, List<string> Deps)>> layers)
    {
        var maxNodesInLayer = layers.Max(l => l.Count);
        var totalWidth = maxNodesInLayer * (NodeWidth + HorizontalSpacing);
        _canvas.Width = Math.Max(totalWidth - HorizontalSpacing, 400);
        _canvas.Height = layers.Count * (NodeHeight + VerticalSpacing) + 40;

        var nodePositions = new Dictionary<string, Point>();

        for (int layerIdx = 0; layerIdx < layers.Count; layerIdx++)
        {
            var layer = layers[layerIdx];
            var layerWidth = layer.Count * (NodeWidth + HorizontalSpacing) - HorizontalSpacing;
            var startX = (totalWidth - layerWidth) / 2;
            if (startX < 10) startX = 10;

            for (int i = 0; i < layer.Count; i++)
            {
                var x = startX + i * (NodeWidth + HorizontalSpacing);
                var y = 20 + layerIdx * (NodeHeight + VerticalSpacing);
                var task = layer[i];
                nodePositions[task.Id] = new Point(x, y);

                DrawNode(x, y, task.Label, task.Status);
            }
        }

        var taskDict = layers.SelectMany(l => l).ToDictionary(t => t.Id);
        foreach (var layer in layers)
        {
            foreach (var task in layer)
            {
                foreach (var depId in task.Deps)
                {
                    if (nodePositions.TryGetValue(depId, out var fromPos) &&
                        nodePositions.TryGetValue(task.Id, out var toPos))
                    {
                        DrawEdge(fromPos, toPos);
                    }
                }
            }
        }
    }

    private void DrawNode(double x, double y, string label, string status)
    {
        var color = status switch
        {
            "Completed" => Color.Parse("#3fb950"),
            "Running" => Color.Parse("#d29922"),
            "Ready" => Color.Parse("#a371f7"),
            "Failed" => Color.Parse("#f85149"),
            _ => Color.Parse("#484f58")
        };

        var rect = new Rectangle
        {
            Width = NodeWidth,
            Height = NodeHeight,
            Fill = new SolidColorBrush(Color.Parse("#161b22")),
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2,
            RadiusX = 6,
            RadiusY = 6
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        _canvas.Children.Add(rect);

        var statusDot = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = new SolidColorBrush(color)
        };
        Canvas.SetLeft(statusDot, x + 6);
        Canvas.SetTop(statusDot, y + NodeHeight / 2 - 5);
        _canvas.Children.Add(statusDot);

        var text = new TextBlock
        {
            Text = label,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            FontFamily = new("Consolas"),
            FontSize = 10,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = NodeWidth - 24,
            VerticalAlignment = VerticalAlignment.Center
        };
        Canvas.SetLeft(text, x + 22);
        Canvas.SetTop(text, y + NodeHeight / 2 - 8);
        _canvas.Children.Add(text);
    }

    private void DrawEdge(Point from, Point to)
    {
        var startX = from.X + NodeWidth;
        var startY = from.Y + NodeHeight / 2;
        var endX = to.X;
        var endY = to.Y + NodeHeight / 2;
        var midX = (startX + endX) / 2;

        var pathFigure = new PathFigure { StartPoint = new Point(startX, startY) };
        pathFigure.Segments!.Add(new BezierSegment
        {
            Point1 = new Point(midX, startY),
            Point2 = new Point(midX, endY),
            Point3 = new Point(endX, endY)
        });

        var pathGeometry = new PathGeometry();
        pathGeometry.Figures!.Add(pathFigure);

        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = pathGeometry,
            Stroke = LtaiTheme.Sbb(LtaiTheme.Border),
            StrokeThickness = 2,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 2 }
        };
        _canvas.Children.Add(path);

        var arrowHead = new Polygon
        {
            Points = new List<Point>
            {
                new(endX, endY),
                new(endX - 8, endY - 5),
                new(endX - 8, endY + 5)
            },
            Fill = LtaiTheme.Sbb(LtaiTheme.Border)
        };
        _canvas.Children.Add(arrowHead);
    }

    private LTAI.Agent.Workflows.LTAICoordinator? TryGetCoordinator()
    {
        try
        {
            return ServiceLocator.Get<LTAI.Agent.Workflows.LTAICoordinator>();
        }
        catch { return null; }
    }

    private LTAI.Agent.Workflows.TaskQueue? TryGetTaskQueue()
    {
        try
        {
            return ServiceLocator.Get<LTAI.Agent.Workflows.TaskQueue>();
        }
        catch { return null; }
    }
}
