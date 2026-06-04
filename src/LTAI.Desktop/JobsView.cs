// Copyright (c) LTAI. All rights reserved.

using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LTAI.Agent.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Desktop;

/// <summary>
/// P14.14: Live job panel. Subscribes to <see cref="BackgroundJobService.JobCompleted"/>
/// for instant status updates + 2 s polling for elapsed-time refresh. Each row has
/// an inline [Cancel] button (disabled once the job completes). Per D72 philosophy:
/// "no double UI" — the source-of-truth lives in BGJS, this view is a read-only
/// window onto it (CI/cron scripts use /ltai/v1/jobs for the same data).
/// </summary>
public sealed class JobsView : UserControl
{
    private readonly LTAIService _svc;
    private readonly DispatcherTimer _refreshTimer;
    private readonly StackPanel _rowsPanel;
    private readonly TextBlock _emptyText;
    private readonly TextBlock _footerText;
    private readonly ScrollViewer _scroll;
    private BackgroundJobService? _jobs;
    private readonly HashSet<string> _seenIds = new();

    public JobsView(LTAIService svc)
    {
        _svc = svc;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new StackPanel { Spacing = 12, Margin = new(16) };

        root.Children.Add(new TextBlock
        {
            Text = "后台作业 (Background Jobs)",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
        });

        // Header row (column titles)
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("50,80,60,80,*,70"),
            Margin = new(0, 4, 0, 0),
        };
        AddHeaderCell(header, 0, "ID");
        AddHeaderCell(header, 1, "状态");
        AddHeaderCell(header, 2, "Exit");
        AddHeaderCell(header, 3, "已运行");
        AddHeaderCell(header, 4, "命令");
        AddHeaderCell(header, 5, "");
        root.Children.Add(header);

        // Rows
        _rowsPanel = new StackPanel { Spacing = 4 };
        _emptyText = new TextBlock
        {
            Text = "暂无后台作业。让 agent 跑一个 long-running shell 命令即可出现。",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 8, 0, 0),
        };
        _rowsPanel.Children.Add(_emptyText);
        _scroll = new ScrollViewer
        {
            Content = _rowsPanel,
            MaxHeight = 460,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        root.Children.Add(_scroll);

        _footerText = new TextBlock
        {
            Text = "—",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontSize = 11,
            Margin = new(0, 4, 0, 0),
        };
        root.Children.Add(_footerText);

        Content = root;

        // Resolve BGJS lazily when attached (App.Services is set in Program.cs)
        AttachedToVisualTree += async (_, _) => await ResolveAsync();
        DetachedFromVisualTree += (_, _) => Unsubscribe();

        // Periodic refresh for elapsed time + newly-started jobs
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            (_, _) => Refresh());
        _refreshTimer.Start();
    }

    private static void AddHeaderCell(Grid grid, int col, string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.Bold,
            FontSize = 11,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
        };
        Grid.SetColumn(tb, col);
        grid.Children.Add(tb);
    }

    private async Task ResolveAsync()
    {
        if (_jobs != null) return;
        try
        {
            var sp = App.Services
                ?? throw new InvalidOperationException("App.Services not initialized");
            _jobs = sp.GetService<BackgroundJobService>();
            if (_jobs != null)
            {
                _jobs.JobCompleted += OnJobCompletedFromBgjs;
            }
        }
        catch (Exception ex)
        {
            _footerText.Text = $"Resolve failed: {ex.Message}";
        }
        Refresh();
        await Task.CompletedTask;
    }

    private void Unsubscribe()
    {
        if (_jobs != null)
        {
            _jobs.JobCompleted -= OnJobCompletedFromBgjs;
        }
    }

    private void OnJobCompletedFromBgjs(string id, JobEntry entry)
    {
        // Force an immediate refresh on the UI thread.
        Dispatcher.UIThread.Post(() => Refresh());
    }

    private void Refresh()
    {
        if (_jobs == null) { _footerText.Text = "BGJS not available"; return; }
        var snap = _jobs.SnapshotJobs();
        var ordered = snap
            .OrderBy(kv => int.TryParse(kv.Key, out var n) ? n : 0)
            .ToList();

        // Track new IDs for the footer
        foreach (var id in ordered.Select(kv => kv.Key))
            _seenIds.Add(id);

        // Rebuild rows
        _rowsPanel.Children.Clear();
        if (ordered.Count == 0)
        {
            _rowsPanel.Children.Add(new TextBlock
            {
                Text = "暂无后台作业。让 agent 跑一个 long-running shell 命令即可出现。",
                Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new(0, 8, 0, 0),
            });
        }
        else
        {
            foreach (var (id, j) in ordered)
            {
                _rowsPanel.Children.Add(BuildRow(id, j));
            }
        }

        var running = ordered.Count(kv => !kv.Value.Completed);
        _footerText.Text = $"共 {ordered.Count} 个作业 ({running} 运行中) · 60s 后自动清理 · 订阅事件: JobCompleted";
    }

    private Grid BuildRow(string id, JobEntry j)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("50,80,60,80,*,70"),
        };

        // ID
        var idCell = new TextBlock
        {
            Text = id,
            FontFamily = LtaiTheme.CodeFont,
            FontSize = 12,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(idCell, 0);
        grid.Children.Add(idCell);

        // Status (icon + label)
        string statusIcon, statusLabel;
        Color statusColor;
        if (!j.Completed) { statusIcon = "⏳"; statusLabel = "运行中"; statusColor = LtaiTheme.AccentWarning; }
        else if (j.ExitCode == 0) { statusIcon = "✅"; statusLabel = "完成"; statusColor = LtaiTheme.AccentSystem; }
        else if (j.Error == "Cancelled") { statusIcon = "🚫"; statusLabel = "取消"; statusColor = LtaiTheme.TextSecondary; }
        else { statusIcon = "❌"; statusLabel = "失败"; statusColor = LtaiTheme.AccentDanger; }
        var statusCell = new TextBlock
        {
            Text = $"{statusIcon} {statusLabel}",
            FontSize = 11,
            Foreground = LtaiTheme.Sbb(statusColor),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(statusCell, 1);
        grid.Children.Add(statusCell);

        // Exit
        var exitCell = new TextBlock
        {
            Text = j.Completed ? (j.ExitCode?.ToString() ?? "?") : "—",
            FontFamily = LtaiTheme.CodeFont,
            FontSize = 11,
            Foreground = LtaiTheme.Sbb(j.Completed && j.ExitCode == 0
                ? LtaiTheme.TextSecondary
                : LtaiTheme.TextDim),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(exitCell, 2);
        grid.Children.Add(exitCell);

        // Elapsed
        var elapsed = DateTime.UtcNow - j.StartedAtUtc;
        var elapsedStr = elapsed.TotalSeconds < 60
            ? $"{(int)elapsed.TotalSeconds}s"
            : $"{elapsed.Minutes}m{elapsed.Seconds}s";
        var elapsedCell = new TextBlock
        {
            Text = elapsedStr,
            FontFamily = LtaiTheme.CodeFont,
            FontSize = 11,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(elapsedCell, 3);
        grid.Children.Add(elapsedCell);

        // Command (truncated)
        var cmd = j.Command ?? "";
        if (cmd.Length > 80) cmd = cmd[..77] + "...";
        var cmdCell = new TextBlock
        {
            Text = cmd,
            FontSize = 10,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontFamily = LtaiTheme.CodeFont,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(cmdCell, 4);
        grid.Children.Add(cmdCell);

        // Cancel button (disabled if completed)
        var cancelBtn = new Button
        {
            Content = "Cancel",
            FontSize = 10,
            Padding = new(6, 2),
            IsEnabled = !j.Completed,
            HorizontalAlignment = HorizontalAlignment.Right,
            Tag = id, // store id for event handler
        };
        cancelBtn.Click += OnCancelClick;
        Grid.SetColumn(cancelBtn, 5);
        grid.Children.Add(cancelBtn);

        return grid;
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } && _jobs != null)
        {
            if (_jobs.SnapshotJobs().TryGetValue(id, out var entry) && !entry.Completed)
            {
                entry.Completed = true;
                entry.Error = "Cancelled";
                Refresh();
            }
        }
    }
}
