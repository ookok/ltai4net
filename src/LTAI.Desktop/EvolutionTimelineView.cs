using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LTAI.AI.Governors;

namespace LTAI.Desktop;

public sealed class EvolutionTimelineView : UserControl
{
    private readonly LTAIService _svc;
    private readonly StackPanel _categoryTabs;
    private readonly StackPanel _timelinePanel;
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _statsText;
    private readonly DispatcherTimer _timer;
    private ICrossRunEvolutionStore? _store;
    private LessonCategory? _activeCategory;

    public EvolutionTimelineView(LTAIService svc)
    {
        _svc = svc;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new DockPanel { Margin = new(16) };

        var header = new TextBlock
        {
            Text = "Evolution Timeline",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo)
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var sep = new Border { Height = 1, Background = LtaiTheme.Sbb(LtaiTheme.Border), Margin = new(0, 8) };
        DockPanel.SetDock(sep, Dock.Top);
        root.Children.Add(sep);

        _categoryTabs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new(0, 0, 0, 8)
        };
        DockPanel.SetDock(_categoryTabs, Dock.Top);
        root.Children.Add(_categoryTabs);

        _timelinePanel = new StackPanel { Spacing = 6 };
        _scroller = new ScrollViewer { Content = _timelinePanel };
        root.Children.Add(_scroller);

        _statsText = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontFamily = new("Consolas"),
            FontSize = 12,
            Margin = new(0, 8, 0, 0)
        };
        DockPanel.SetDock(_statsText, Dock.Bottom);
        root.Children.Add(_statsText);

        Content = root;

        InitStore();

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();

        _activeCategory = null;
        BuildCategoryTabs();
        Refresh();
    }

    private void InitStore()
    {
        try
        {
            var ltsType = _svc.LTS.GetType();
            var field = ltsType.GetField("_crossRunEvo",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _store = field?.GetValue(_svc.LTS) as ICrossRunEvolutionStore;
        }
        catch { }
    }

    private void BuildCategoryTabs()
    {
        _categoryTabs.Children.Clear();

        var allBtn = CreateTab("All", null);
        _categoryTabs.Children.Add(allBtn);

        var categories = new[]
        {
            (LessonCategory.QualityRegression, "Quality"),
            (LessonCategory.SafetyViolation, "Safety"),
            (LessonCategory.BudgetExhausted, "Budget"),
            (LessonCategory.RoutingError, "Routing"),
            (LessonCategory.ContextOverflow, "Context"),
            (LessonCategory.ExecutionTimeout, "Timeout"),
            (LessonCategory.ExperimentFailure, "Experiment"),
            (LessonCategory.ModelDegradation, "Model"),
            (LessonCategory.DependencyConflict, "Deps"),
            (LessonCategory.DataDrift, "Drift"),
            (LessonCategory.GeneralWarning, "General"),
        };

        foreach (var (cat, label) in categories)
        {
            var btn = CreateTab(label, cat);
            _categoryTabs.Children.Add(btn);
        }
    }

    private Button CreateTab(string label, LessonCategory? category)
    {
        var btn = new Button
        {
            Content = label,
            Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11,
            Height = 26,
            Padding = new(10, 0),
            BorderThickness = new(1),
            BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
            CornerRadius = new(4)
        };
        btn.Click += (_, _) =>
        {
            _activeCategory = category;
            foreach (var child in _categoryTabs.Children)
            {
                if (child is Button b)
                {
                    var isActive = (category == null && b.Content?.ToString() == "All") ||
                                   (category != null && b.Content?.ToString() == label);
                    b.Background = LtaiTheme.Sbb(isActive ? LtaiTheme.AccentDNA : LtaiTheme.BgPanel);
                    b.Foreground = LtaiTheme.Sbb(isActive ? Colors.White : LtaiTheme.TextSecondary);
                }
            }
            Refresh();
        };
        return btn;
    }

    private void Refresh()
    {
        if (_store == null)
        {
            InitStore();
        }

        if (_store == null)
        {
            _timelinePanel.Children.Clear();
            _timelinePanel.Children.Add(new TextBlock
            {
                Text = "Cross-run evolution store not found",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontSize = 12
            });
            _statsText.Text = "No evolution data available";
            return;
        }

        List<EvolutionLesson> lessons;
        if (_activeCategory.HasValue)
            lessons = _store.GetLessonsByCategory(_activeCategory.Value, 50);
        else
            lessons = _store.GetActiveLessons(50);

        var sorted = lessons.OrderByDescending(l => l.RecordedAt).ToList();

        _timelinePanel.Children.Clear();

        if (sorted.Count == 0)
        {
            _timelinePanel.Children.Add(new TextBlock
            {
                Text = "No lessons recorded yet",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontSize = 12,
                Margin = new(0, 16)
            });
        }
        else
        {
            foreach (var lesson in sorted)
            {
                var severityPct = (int)(lesson.Severity * 100);
                var barColor = lesson.Severity > 0.7 ? LtaiTheme.AccentDanger :
                               lesson.Severity > 0.4 ? LtaiTheme.AccentWarning :
                               LtaiTheme.AccentInfo;

                var entryBorder = new Border
                {
                    Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
                    BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
                    BorderThickness = new(1),
                    CornerRadius = new(6),
                    Padding = new(10),
                    Margin = new(0, 2)
                };

                var entryStack = new StackPanel { Spacing = 4 };

                var topRow = new DockPanel();
                var severityBar = new Border
                {
                    Width = Math.Max(4, severityPct),
                    Height = 8,
                    Background = LtaiTheme.Sbb(barColor),
                    CornerRadius = new(3)
                };
                var severityLabel = new TextBlock
                {
                    Text = $"{severityPct}%",
                    Foreground = LtaiTheme.Sbb(barColor),
                    FontSize = 11,
                    FontWeight = FontWeight.Bold,
                    FontFamily = new("Consolas")
                };
                var mitigatedBadge = new TextBlock
                {
                    Text = lesson.AppliedCount > 0 ? "✓ Mitigated" : "○ Pending",
                    Foreground = LtaiTheme.Sbb(lesson.AppliedCount > 0 ? LtaiTheme.AccentSystem : LtaiTheme.TextDim),
                    FontSize = 10
                };
                DockPanel.SetDock(mitigatedBadge, Dock.Right);
                DockPanel.SetDock(severityLabel, Dock.Right);

                var catBlock = new TextBlock
                {
                    Text = $"  {lesson.Category}",
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                    FontSize = 10,
                    FontStyle = FontStyle.Italic
                };

                topRow.Children.Add(severityBar);
                topRow.Children.Add(catBlock);
                topRow.Children.Add(severityLabel);
                topRow.Children.Add(mitigatedBadge);

                entryStack.Children.Add(topRow);

                entryStack.Children.Add(new TextBlock
                {
                    Text = lesson.Summary,
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                });

                if (!string.IsNullOrWhiteSpace(lesson.Mitigation))
                {
                    entryStack.Children.Add(new TextBlock
                    {
                        Text = $"Mitigation: {lesson.Mitigation}",
                        Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
                        FontSize = 11,
                        FontStyle = FontStyle.Italic,
                        TextWrapping = TextWrapping.Wrap
                    });
                }

                var bottomRow = new DockPanel { Margin = new(0, 2, 0, 0) };
                var sourceText = !string.IsNullOrWhiteSpace(lesson.SourceRun)
                    ? $"Run: {lesson.SourceRun}"
                    : "";
                if (!string.IsNullOrWhiteSpace(lesson.SourceStage))
                    sourceText += string.IsNullOrEmpty(sourceText) ? lesson.SourceStage : $" | {lesson.SourceStage}";

                bottomRow.Children.Add(new TextBlock
                {
                    Text = lesson.RecordedAt.ToString("yyyy-MM-dd HH:mm"),
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                    FontSize = 10,
                    FontFamily = new("Consolas")
                });

                var sourceBlock = new TextBlock
                {
                    Text = sourceText,
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                    FontSize = 10,
                    FontFamily = new("Consolas")
                };
                DockPanel.SetDock(sourceBlock, Dock.Right);
                bottomRow.Children.Add(sourceBlock);

                entryStack.Children.Add(bottomRow);

                entryBorder.Child = entryStack;
                _timelinePanel.Children.Add(entryBorder);
            }
        }

        var total = _store.LessonCount;
        var active = _store.ActiveLessonCount;
        _statsText.Text = $"Total lessons: {total}  |  Active: {active}  |  Filter: {(_activeCategory?.ToString() ?? "All")}";
    }
}
