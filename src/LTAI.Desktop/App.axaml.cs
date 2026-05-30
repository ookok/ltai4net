using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace LTAI.Desktop;

public class App : Application
{
    /// <summary>
    /// LTAI service instance, set by Program.Main() before Avalonia starts.
    /// Accessed by MainWindow and views that need it.
    /// </summary>
    public static LTAIService? Ltais { get; set; }
    public static LTAI.Agent.ChatAgent? ChatAgent { get; set; }
    public static Microsoft.Extensions.Options.IOptions<LTAI.Core.Configuration.LTAIOptions>? Options { get; set; }
    public static LTAI.AI.MultiProviderChatClient? Router { get; set; }
    public static System.Net.Http.IHttpClientFactory? HttpFactory { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Show window immediately with loading state
            var window = new MainWindow(null);
            desktop.MainWindow = window;

            // Initialize services in background (DI chain + warmup)
            _ = Task.Run(async () =>
            {
                await Program.InitializeServicesAsync();
                // Update window on UI thread when ready
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    window.DataContext = Ltais;
                });
            });
        }
        base.OnFrameworkInitializationCompleted();
    }
}
