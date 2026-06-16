using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace LTAI.Accelerator;

public partial class MainWindow : Window
{
    /// <summary>Cloudflare WARP 官方下载地址（首选）</summary>
    private const string WarpOfficialUrl = "https://downloads.cloudflareclient.com/v1/download/windows/ga";
    /// <summary>国内镜像，从 appsettings.json LTAI:Mirrors:WarpMsiUrl 读取</summary>
    private readonly string WarpMirrorUrl;

    private ProxyService? _proxy;
    private const int ProxyPort = 11818;
    private TrayIcon? _trayIcon;

    public MainWindow()
    {
        var cfg = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();
        WarpMirrorUrl = cfg.GetSection("LTAI:Mirrors:WarpMsiUrl").Value
            ?? "http://mogoo.com.cn/Cloudflare_WARP_2026.4.1390.0.msi";
        InitializeComponent();
        SetupTrayIcon();

        if (WarpService.FindWarpCli() != null)
        {
            WarpInfo.Text = "已就绪 ✓";
            WarpInfo.Foreground = new SolidColorBrush(Color.Parse("#4caf50"));
            BtnInstallWarp.IsVisible = false;
        }
        else
        {
            WarpInfo.Text = "未安装 — 点击下载";
            WarpInfo.Foreground = new SolidColorBrush(Color.Parse("#888"));
            BtnInstallWarp.IsVisible = true;
        }

        SetStatusDot("#666");
    }

    private void SetupTrayIcon()
    {
        try
        {
            var icon = new WindowIcon(
                Avalonia.Platform.AssetLoader.Open(new Uri("avares://LTAI.Accelerator/Assets/ltai-icon.ico")));

            var menu = new NativeMenu();
            var showItem = new NativeMenuItem("显示窗口");
            showItem.Click += (_, _) => { Show(); Activate(); WindowState = WindowState.Normal; };
            menu.Add(showItem);

            menu.Add(new NativeMenuItemSeparator());

            var exitItem = new NativeMenuItem("退出");
            exitItem.Click += async (_, _) =>
            {
                _trayIcon?.Dispose();
                if (_proxy != null)
                {
                    SystemProxy.Disable();
                    await _proxy.StopAsync();
                    _proxy.Dispose();
                    _proxy = null;
                }
                Environment.Exit(0);
            };
            menu.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                Icon = icon,
                ToolTipText = "LTAI 加速器",
                Menu = menu,
                IsVisible = true,
            };
            _trayIcon.Clicked += (_, _) => { Show(); Activate(); WindowState = WindowState.Normal; };
        }
        catch
        {
            // non-critical, best-effort
        }
    }

    private async void OnStartClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            BtnStart.IsEnabled = false;
            SetStatus("正在启动...", "#ffa000");
            SetStatusDot("#ffa000");

            _proxy = new ProxyService(ProxyPort);
            await _proxy.StartAsync();

            SystemProxy.Enable($"http://127.0.0.1:{ProxyPort}");

            // Try WARP as upstream for international traffic
            if (_proxy.WarpConnectTask != null)
            {
                SetStatus("正在连接 Cloudflare WARP...", "#ffa000");
                var warpOk = await _proxy.WarpConnectTask;
                if (!warpOk)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(5000);
                            if (_proxy?.Warp.Available == true && !_proxy.Warp.Connected)
                                await _proxy.Warp.ConnectAsync();
                        }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"WARP reconnect error: {ex.Message}"); }
                    });
                }
            }

            if (_proxy.Warp.Connected)
            {
                WarpInfo.Text = "已连接 ✓ — 全局隧道";
                WarpInfo.Foreground = new SolidColorBrush(Color.Parse("#4caf50"));
                BtnInstallWarp.IsVisible = false;
                SetStatus("WARP 全局加速 (国内直连)", "#4caf50");
                SetStatusDot("#4caf50");
            }
            else if (_proxy.Warp.Available)
            {
                WarpInfo.Text = "连接失败，仅 DoH 模式";
                WarpInfo.Foreground = new SolidColorBrush(Color.Parse("#ff9800"));
                BtnInstallWarp.IsVisible = false;
                SetStatus("系统代理 :11818 (国内直连)", "#4caf50");
                SetStatusDot("#4caf50");
            }
            else
            {
                WarpInfo.Text = "未安装 — 点击按钮下载安装";
                WarpInfo.Foreground = new SolidColorBrush(Color.Parse("#888"));
                BtnInstallWarp.IsVisible = true;
                SetStatus("系统代理 :11818 (国内直连)", "#4caf50");
                SetStatusDot("#4caf50");
            }

            BtnStop.IsEnabled = true;
        }
        catch (Exception ex)
        {
            SetStatus($"启动失败: {ex.Message}", "#c62828");
            SetStatusDot("#c62828");
            BtnStart.IsEnabled = true;
        }
    }

    private async void OnStopClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            BtnStop.IsEnabled = false;
            SetStatus("正在停止...", "#ffa000");
            SetStatusDot("#ffa000");

            if (_proxy != null)
            {
                await _proxy.StopAsync();
                _proxy.Dispose();
                _proxy = null;
            }

            SystemProxy.Disable();

            if (WarpService.FindWarpCli() != null)
            {
                WarpInfo.Text = "已断开 — 点击即可重新连接";
                WarpInfo.Foreground = new SolidColorBrush(Color.Parse("#888"));
                BtnInstallWarp.IsVisible = false;
            }
            else
            {
                WarpInfo.Text = "未安装 — 点击下载";
                WarpInfo.Foreground = new SolidColorBrush(Color.Parse("#888"));
                BtnInstallWarp.IsVisible = true;
            }

            BtnStart.IsEnabled = true;
            SetStatus("已停止", "#666");
            SetStatusDot("#666");
        }
        catch (Exception ex)
        {
            SetStatus($"停止出错: {ex.Message}", "#c62828");
            SetStatusDot("#c62828");
            BtnStop.IsEnabled = true;
        }
    }

    private void SetStatus(string text, string color)
    {
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(Color.Parse(color));
    }

    private void SetStatusDot(string color)
    {
        StatusDot.Fill = new SolidColorBrush(Color.Parse(color));
    }

    private void OnWarpClick(object? sender, PointerPressedEventArgs e)
    {
        var psi = new ProcessStartInfo
        {
            FileName = WarpOfficialUrl,
            UseShellExecute = true
        };
        Process.Start(psi);
    }

    private void OnProxyClick(object? sender, PointerPressedEventArgs e) { }

    private void OnInstallWarpClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Validate mirror URL: HTTPS only
        if (!string.IsNullOrEmpty(WarpMirrorUrl) && WarpMirrorUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("WARP mirror must use HTTPS (HTTP not allowed for security)", "#ff0000");
            return;
        }

        var tmpScript = Path.Combine(Path.GetTempPath(), "ltai-install-warp.ps1");
        var psLines = new[]
        {
            "$urls = @(",
            $"  '{WarpOfficialUrl}',",
            $"  '{WarpMirrorUrl}'",
            ")",
            "$msi = \"$env:TEMP\\Cloudflare_WARP.msi\"",
            "$ok = $false",
            "foreach ($url in $urls) {",
            "  Write-Host \"Trying $url...\" -ForegroundColor Cyan",
            "  try {",
            "    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12",
            "    $wc = New-Object Net.WebClient",
            "    $wc.Headers.Add('User-Agent', 'LTAI-Accelerator/1.0')",
            "    $wc.DownloadFile($url, $msi)",
            "    if ((Get-Item $msi -ErrorAction SilentlyContinue).Length -ge 1MB) { $ok=$true; break }",
            "  } catch { Write-Host \"  failed: $_\" -ForegroundColor Red }",
            "}",
            "if (-not $ok) { Write-Host 'All sources failed' -ForegroundColor Red; exit 1 }",
            "Write-Host 'Installing (msiexec /quiet)...' -ForegroundColor Cyan",
            "$p = Start-Process msiexec.exe -ArgumentList \"/i `\"$msi`\" /quiet /norestart\" -Wait -PassThru",
            "if ($p.ExitCode -eq 0) { Write-Host 'WARP installed! Restart LTAI Accelerator.' -ForegroundColor Green }",
            "else { Write-Host \"msiexec failed (exit=$($p.ExitCode))\" -ForegroundColor Red }",
            "Remove-Item $msi -Force -ErrorAction SilentlyContinue",
            "Start-Sleep -Seconds 5",
        };
        File.WriteAllText(tmpScript, string.Join("\r\n", psLines));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tmpScript}\"",
            Verb = "runas",
            UseShellExecute = true,
        };
        Process.Start(psi);
        SetStatus("正在安装 Cloudflare WARP... 请查看弹出的 PowerShell 窗口", "#ffa000");
        SetStatusDot("#ffa000");
    }

    // Minimize to tray instead of closing
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_trayIcon != null)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        // No tray icon → exit normally
        Cleanup();
        base.OnClosing(e);
    }

    private void Cleanup()
    {
        SystemProxy.Disable();
        _proxy?.Dispose();
        _proxy = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
