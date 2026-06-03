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
    }

    private async void OnStartClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            BtnStart.IsEnabled = false;
            SetStatus("正在启动...", "#ffa000");

            _proxy = new ProxyService(ProxyPort);
            await _proxy.StartAsync();

            SystemProxy.Enable($"http://127.0.0.1:{ProxyPort}");

            if (_proxy.Warp.Connected)
                WarpInfo.Text = "Cloudflare Warp: 已连接 ✓ — 全局隧道";
            else if (_proxy.Warp.Available)
                WarpInfo.Text = "Cloudflare Warp: 连接失败，仅 DoH 模式";
            else
                WarpInfo.Text = "Cloudflare Warp: 未安装 — 点击下载 https://downloads.cloudflareclient.com/";

            BtnStop.IsEnabled = true;
            SetStatus("运行中 — 系统代理已开启", "#2e7d32");
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
