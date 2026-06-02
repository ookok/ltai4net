using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

namespace LTAI.Desktop;

public class App : Application
{
    public static LTAIService? Ltais { get; set; }
    public static LTAI.Agent.ChatAgent? ChatAgent { get; set; }
    public static Microsoft.Extensions.Options.IOptions<LTAI.Core.Configuration.LTAIOptions>? Options { get; set; }
    public static LTAI.AI.MultiProviderChatClient? Router { get; set; }
    public static System.Net.Http.IHttpClientFactory? HttpFactory { get; set; }
    public static System.IServiceProvider? Services { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Run service init on threadpool, then post MainWindow creation back to UI
            // This avoids blocking the UI thread during DI warmup (which can take 5-10s).
            Task.Run(async () =>
            {
                try
                {
                    await Program.InitializeServicesAsync().ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var window = new MainWindow(App.Ltais!);
                        desktop.MainWindow = window;
                    });
                }
                catch (Exception ex)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var errWindow = new Window
                        {
                            Title = "LTAI — 初始化失败",
                            Width = 500, Height = 200,
                            Content = new TextBlock
                            {
                                Text = $"服务初始化失败:\n{ex.Message}\n\n请检查网络连接和配置后重试。",
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = new SolidColorBrush(Colors.White),
                                Margin = new(20)
                            }
                        };
                        desktop.MainWindow = errWindow;
                        errWindow.Show();
                    });
                }
            });
        }
        base.OnFrameworkInitializationCompleted();
    }
}
