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

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(Ltais!);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
