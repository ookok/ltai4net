using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

namespace LTAI.Desktop.Tests;

/// <summary>
/// Base class for Avalonia headless UI tests.
/// Relies on AvaloniaHeadlessFixture (via collection) for platform initialization.
/// </summary>
public class AvaloniaUITestBase
{
    /// <summary>Create and show a window in headless mode.</summary>
    protected static Window? CreateWindow(Control content, int width = 800, int height = 600)
    {
        AvaloniaHeadlessFixture.EnsurePlatform();
        try
        {
            var w = new Window { Content = content, Width = width, Height = height };
            w.Show();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Normal);
            return w;
        }
        catch
        {
            // Cursor factory may not be available if Avalonia was initialized
            // by a non-headless test running in parallel. Tests should check
            // for null return and skip accordingly.
            return null;
        }
    }
}
