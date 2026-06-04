using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LTAI.Desktop.DevUI;
using LTAI.Desktop.ViewModels;

namespace LTAI.Desktop;

public sealed class DashboardView : UserControl
{
    private readonly DashboardViewModel _vm;
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _devUiStatusText;
    private readonly Lazy<DevUIHost> _devUiHostLazy = new(() => new DevUIHost());

    public DashboardView(LTAIService svc)
    {
        _vm = new DashboardViewModel(svc);
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new StackPanel { Spacing = 12, Margin = new(16) };
        root.Children.Add(new TextBlock
        {
            Text = "LTAI 仪表盘",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary)
        });

        var (sysText, _) = AddPanel(root, "系统");
        var (healthText, _) = AddPanel(root, "运行时");
        var (sessionText, sessionBorder) = AddPanel(root, "会话");

        var contextLabel = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11,
        };
        var contextBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 12,
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            Background = LtaiTheme.Sbb(LtaiTheme.SurfaceOverlay),
            Margin = new(0, 2, 0, 0),
        };
        var cacheLabel = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11,
        };
        var cacheBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 12,
            Foreground = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
            Background = LtaiTheme.Sbb(LtaiTheme.SurfaceOverlay),
            Margin = new(0, 2, 0, 0),
        };

        var sessionContent = new StackPanel { Spacing = 2 };
        sessionContent.Children.Add(sessionText);
        sessionContent.Children.Add(contextLabel);
        sessionContent.Children.Add(contextBar);
        sessionContent.Children.Add(cacheLabel);
        sessionContent.Children.Add(cacheBar);
        sessionBorder.Child = sessionContent;

        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(_vm.SysInfo):
                    sysText.Text = _vm.SysInfo;
                    break;
                case nameof(_vm.HealthInfo):
                    healthText.Text = _vm.HealthInfo;
                    break;
                case nameof(_vm.SessionInfo):
                    sessionText.Text = _vm.SessionInfo;
                    break;
                case nameof(_vm.ContextRatio):
                    contextBar.Value = _vm.ContextRatio;
                    break;
                case nameof(_vm.ContextLabel):
                    contextLabel.Text = _vm.ContextLabel;
                    break;
                case nameof(_vm.CacheHitRate):
                    cacheBar.Value = _vm.CacheHitRate;
                    break;
                case nameof(_vm.CacheLabel):
                    cacheLabel.Text = _vm.CacheLabel;
                    break;
                case nameof(_vm.DevUiStatus):
                    _devUiStatusText.Text = _vm.DevUiStatus;
                    break;
                case nameof(_vm.DevUiStatusVisible):
                    _devUiStatusText.IsVisible = _vm.DevUiStatusVisible;
                    break;
            }
        };

        var devUiBtn = new Button
        {
            Content = "Open DevUI in Browser",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new(0, 8, 0, 0),
        };
        devUiBtn.Click += async (_, _) =>
        {
            var host = _devUiHostLazy.Value;
            try
            {
                var sp = App.Services
                    ?? throw new InvalidOperationException("App.Services not initialized");
                if (host.BaseUrl is null)
                    await host.StartAsync(sp);
                host.OpenInBrowser();
                _vm.SetDevUiStatus($"[DevUI] running at {host.BaseUrl}/devui (browser-launched)", true);
            }
            catch (Exception ex)
            {
                _vm.SetDevUiStatus($"[DevUI] failed: {ex.Message}", true);
            }
        };
        _devUiStatusText = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            FontSize = 11,
            IsVisible = false,
        };
        root.Children.Add(devUiBtn);
        root.Children.Add(_devUiStatusText);

        Content = root;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => _vm.Refresh());
        _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        AttachedToVisualTree += (_, _) =>
        {
            _vm.Refresh();
            if (!_timer.IsEnabled) _timer.Start();
        };
        _vm.Refresh();
    }

    private static (TextBlock, Border) AddPanel(StackPanel parent, string title)
    {
        var tb = new TextBlock { Text = title, FontWeight = FontWeight.Bold, Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) };
        var contentTb = new TextBlock { Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary), TextWrapping = TextWrapping.Wrap };
        var border = new Border { BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border), BorderThickness = new(1), Padding = new(8), Child = contentTb };
        var panel = new StackPanel { Spacing = 6, Children = { tb, border } };
        parent.Children.Add(panel);
        return (contentTb, border);
    }
}
