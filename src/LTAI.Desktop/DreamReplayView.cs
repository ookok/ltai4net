using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LTAI.Desktop;

public sealed class DreamReplayView : UserControl
{
    private readonly LTAIService _svc;
    private readonly TextBlock _moonDisplay;
    private readonly StackPanel _dreamList;
    private readonly ScrollViewer _scroller;
    private readonly DispatcherTimer _timer;

    private static readonly string[] MoonPhases = { "🌑", "🌒", "🌓", "🌔", "🌕", "🌖", "🌗", "🌘" };

    public DreamReplayView(LTAIService svc)
    {
        _svc = svc;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new DockPanel { Margin = new(16) };

        var header = new TextBlock
        {
            Text = "Dream Replay",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo)
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var sep = new Border { Height = 1, Background = LtaiTheme.Sbb(LtaiTheme.Border), Margin = new(0, 8) };
        DockPanel.SetDock(sep, Dock.Top);
        root.Children.Add(sep);

        _moonDisplay = new TextBlock
        {
            FontSize = 48,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new(0, 8)
        };
        DockPanel.SetDock(_moonDisplay, Dock.Top);
        root.Children.Add(_moonDisplay);

        _dreamList = new StackPanel { Spacing = 6 };
        _scroller = new ScrollViewer { Content = _dreamList };
        root.Children.Add(_scroller);

        Content = root;

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        Refresh();
    }

    private void Refresh()
    {
        var today = DateTime.Now;
        var phaseIdx = (int)(today.DayOfYear % 29.53 * 8 / 29.53) % 8;
        _moonDisplay.Text = $"{MoonPhases[phaseIdx]}  {(MoonPhases[phaseIdx] == "🌑" ? "New Moon" :
            MoonPhases[phaseIdx] == "🌕" ? "Full Moon" : "Dream Phase")}";

        _dreamList.Children.Clear();

        var dcField = _svc.LTS.GetType().GetField("_dreamCycle",
            BindingFlags.NonPublic | BindingFlags.Instance);

        if (dcField == null)
        {
            _dreamList.Children.Add(new TextBlock
            {
                Text = "DreamCycle not found",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontSize = 12
            });
            return;
        }

        var dreamCycle = dcField.GetValue(_svc.LTS);
        if (dreamCycle == null)
        {
            _dreamList.Children.Add(new TextBlock
            {
                Text = "DreamCycle is null",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontSize = 12
            });
            return;
        }

        var logPathField = dreamCycle.GetType().GetField("_dreamLogPath",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var logPath = logPathField?.GetValue(dreamCycle) as string;

        if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
        {
            _dreamList.Children.Add(new TextBlock
            {
                Text = $"No dream log at: {logPath ?? "unknown"}",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontSize = 12
            });
            return;
        }

        try
        {
            var json = File.ReadAllText(logPath);
            var dreams = JsonSerializer.Deserialize<List<JsonElement>>(json) ?? new();
            var entries = dreams.ToList();
            entries.Reverse();

            if (entries.Count == 0)
            {
                _dreamList.Children.Add(new TextBlock
                {
                    Text = "No dream records yet",
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                    FontSize = 12
                });
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                var dream = entries[i];
                var bgColor = i % 2 == 0 ? LtaiTheme.BgPanel : Color.Parse(LtaiTheme.Current == AppTheme.Dark ? "#1a2030" : "#eef0f3");

                var summary = "Dream entry";
                var details = new List<string>();

                if (dream.TryGetProperty("MemoriesConsolidated", out var mc))
                    details.Add($"Consolidated: {mc.GetInt32()}");
                if (dream.TryGetProperty("MemoriesForgotten", out var mf))
                    details.Add($"Forgotten: {mf.GetInt32()}");
                if (dream.TryGetProperty("PatternsMerged", out var pm))
                    details.Add($"Patterns: {pm.GetInt32()}");
                if (dream.TryGetProperty("KnowledgeDistilled", out var kd))
                    details.Add($"Distilled: {kd.GetInt32()}");
                if (dream.TryGetProperty("ReflectionsGenerated", out var rg))
                    details.Add($"Reflections: {rg.GetInt32()}");
                if (dream.TryGetProperty("Summary", out var s) && s.GetString() is string sumStr && !string.IsNullOrWhiteSpace(sumStr))
                    summary = sumStr;

                var idxText = entries.Count - i;

                var entryBorder = new Border
                {
                    Background = LtaiTheme.Sbb(bgColor),
                    BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
                    BorderThickness = new(1),
                    CornerRadius = new(6),
                    Padding = new(10)
                };

                var entryStack = new StackPanel { Spacing = 3 };

                var headerRow = new DockPanel();
                var indexBlock = new TextBlock
                {
                    Text = $"Dream #{idxText}",
                    Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo),
                    FontSize = 13,
                    FontWeight = FontWeight.Bold
                };
                var detailBlock = new TextBlock
                {
                    Text = string.Join("  |  ", details),
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                    FontSize = 10,
                    FontFamily = new("Consolas")
                };
                DockPanel.SetDock(detailBlock, Dock.Right);
                headerRow.Children.Add(indexBlock);
                headerRow.Children.Add(detailBlock);

                entryStack.Children.Add(headerRow);
                entryStack.Children.Add(new TextBlock
                {
                    Text = summary,
                    Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                });

                entryBorder.Child = entryStack;
                _dreamList.Children.Add(entryBorder);
            }
        }
        catch (Exception ex)
        {
            _dreamList.Children.Add(new TextBlock
            {
                Text = $"Error loading dreams: {ex.Message}",
                Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDanger),
                FontSize = 12
            });
        }
    }
}
