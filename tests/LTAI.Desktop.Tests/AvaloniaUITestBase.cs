using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

namespace LTAI.Desktop.Tests;

/// <summary>
/// Base class for Avalonia headless UI tests.
/// Configures headless platform once per test run via static initializer.
/// </summary>
public class AvaloniaUITestBase : IDisposable
{
    /// <summary>
    /// Static initializer runs once per test run (before any test).
    /// </summary>
    static AvaloniaUITestBase()
    {
        AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();
    }

    /// <summary>Create and show a window in headless mode.</summary>
    protected static Window CreateWindow(Control content, int width = 800, int height = 600)
    {
        var w = new Window { Content = content, Width = width, Height = height };
        w.Show();
        // Pump dispatcher to ensure visual tree is built
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Normal);
        return w;
    }

    public void Dispose() { }
}
