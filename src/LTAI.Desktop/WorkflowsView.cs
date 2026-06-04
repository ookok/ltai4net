using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LTAI.Agent.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Desktop;

/// <summary>
/// P15 minimal workflow control panel. Per the user's "keep it small" rule
/// (D72): this view is intentionally NOT a full workflow manager. It only
/// surfaces:
///
///   - One [Reload All] button (re-parse every .yaml/.json in the watch dir)
///   - One [Open in DevUI] button (launches P9.2 in-process Kestrel + browser)
///   - A live error banner subscribed to <see cref="WorkflowHotReloadNotifier"/>
///   - A status line: workflow count + last successful reload
///
/// For workflow list / diff / source preview users go to the browser DevUI
/// (per D73 — avoid duplicating the list view in two places).
/// </summary>
public sealed class WorkflowsView : UserControl
{
    private readonly LTAIService _svc;
    private readonly TextBlock _statusText;
    private readonly TextBlock _errorText;
    private readonly TextBlock _lastReloadText;
    private readonly Button _reloadAllBtn;
    private readonly Button _goDevUiBtn;
    private YAMLWorkflowRegistry? _registry;
    private WorkflowHotReloadNotifier? _notifier;
    private Guid _subToken;
    private readonly DispatcherTimer _refreshTimer;

    public WorkflowsView(LTAIService svc)
    {
        _svc = svc;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new StackPanel { Spacing = 12, Margin = new(16) };

        root.Children.Add(new TextBlock
        {
            Text = "热改编排 (Hot-editable Workflows)",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary)
        });

        // Status / count line
        (_statusText, _) = AddPanel(root, "已加载");
        (_lastReloadText, _) = AddPanel(root, "上次重载");
        (_errorText, _) = AddPanel(root, "错误");

        // Action buttons
        _reloadAllBtn = new Button
        {
            Content = "🔄 Reload All",
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 160,
            Margin = new(0, 4, 0, 0),
        };
        _reloadAllBtn.Click += async (_, _) => await ReloadAllAsync();
        root.Children.Add(_reloadAllBtn);

        _goDevUiBtn = new Button
        {
            Content = "🔬 Open DevUI View",
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 160,
            Margin = new(0, 4, 0, 0),
        };
        _goDevUiBtn.Click += (_, _) => NavigateToDevUI();
        root.Children.Add(_goDevUiBtn);

        // Footer hint
        root.Children.Add(new TextBlock
        {
            Text = "提示：编辑 .livingtree/workflows/*.yaml 后保存即可热加载。\n" +
                   "完整列表 / 源码预览请使用 DevUI 视图 (Ctrl+1)。",
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 8, 0, 0),
        });

        Content = root;

        // Subscribe to reload events when the view is attached (DI lookup).
        AttachedToVisualTree += async (_, _) => await ResolveAsync();
        DetachedFromVisualTree += (_, _) => Unsubscribe();

        // Periodic status refresh (count + last reload time)
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            (_, _) => RefreshStatus());
        _refreshTimer.Start();
    }

    private async Task ResolveAsync()
    {
        if (_registry != null) return;
        try
        {
            var sp = App.Services
                ?? throw new InvalidOperationException("App.Services not initialized");
            _registry = sp.GetService<YAMLWorkflowRegistry>();
            _notifier = sp.GetService<WorkflowHotReloadNotifier>();
            if (_notifier != null)
            {
                _subToken = _notifier.Subscribe(new SubscriberBridge(this));
            }
        }
        catch (Exception ex)
        {
            _errorText.Text = $"Resolve failed: {ex.Message}";
        }
        await Task.CompletedTask;
    }

    private void Unsubscribe()
    {
        _refreshTimer.Stop();
        if (_notifier != null && _subToken != Guid.Empty)
        {
            _notifier.Unsubscribe(_subToken);
            _subToken = Guid.Empty;
        }
    }

    private async Task ReloadAllAsync()
    {
        if (_registry == null) { _errorText.Text = "Registry not available"; return; }
        try
        {
            await _registry.ReloadAllAsync();
            _errorText.Text = "—";
        }
        catch (Exception ex)
        {
            _errorText.Text = $"Reload failed: {ex.Message}";
        }
    }

    private static void NavigateToDevUI()
    {
        // Find the MainWindow and switch to DevUI view (index 0)
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is MainWindow mw)
            {
                Dispatcher.UIThread.Post(() => mw.SwitchToView(0));
            }
        }
    }

    private void RefreshStatus()
    {
        if (_registry == null) return;
        var list = _registry.List();
        _statusText.Text = $"{list.Count} 个 workflow · 目录: {_registry.WatchDirectory}";
        if (list.Count > 0)
        {
            var mostRecent = list.MaxBy(w => w.LoadedAtUtc);
            _lastReloadText.Text = $"{mostRecent.Name} @ {mostRecent.LoadedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        }
        else
        {
            _lastReloadText.Text = "—";
        }
    }

    internal void OnReloadedFromNotifier(WorkflowReloadEvent evt)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _lastReloadText.Text = $"{evt.Name} @ {evt.ReloadedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            _errorText.Text = "—";
        });
    }

    internal void OnLoadFailedFromNotifier(WorkflowLoadFailedEvent evt)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _errorText.Text = $"[{evt.Name}] {evt.Reason}";
        });
    }

    private static (TextBlock, Border) AddPanel(StackPanel parent, string title)
    {
        var tb = new TextBlock { Text = title, FontWeight = FontWeight.Bold, Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) };
        var contentTb = new TextBlock { Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary), TextWrapping = TextWrapping.Wrap, Text = "—" };
        var border = new Border { BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border), BorderThickness = new(1), Padding = new(8), Child = contentTb };
        var panel = new StackPanel { Spacing = 4, Children = { tb, border } };
        parent.Children.Add(panel);
        return (contentTb, border);
    }

    /// <summary>Bridge: WorkflowHotReloadNotifier → WorkflowsView. Public method wrappers required by interface.</summary>
    private sealed class SubscriberBridge : IWorkflowSubscriber
    {
        private readonly WorkflowsView _view;
        public SubscriberBridge(WorkflowsView view) { _view = view; }
        public void OnReloaded(WorkflowReloadEvent evt) => _view.OnReloadedFromNotifier(evt);
        public void OnLoadFailed(WorkflowLoadFailedEvent evt) => _view.OnLoadFailedFromNotifier(evt);
    }
}
