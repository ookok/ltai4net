using Avalonia;
using Avalonia.Headless;

namespace LTAI.Desktop.Tests;

/// <summary>Ensures Avalonia headless platform is initialized once before any
/// test in the collection runs. Used via [CollectionDefinition].</summary>
public sealed class AvaloniaHeadlessFixture : IDisposable
{
    private static bool _initialized;
    private static readonly object _lock = new();

    public AvaloniaHeadlessFixture() => EnsurePlatform();

    public static void EnsurePlatform()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;
            try
            {
                AppBuilder.Configure<TestApp>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                    .SetupWithoutStarting();
            }
            catch
            {
                // Platform already initialized by another test — that's fine.
                // Cursor factory may not be available in non-headless mode;
                // CreateWindow handles this with SkipException.
            }
            _initialized = true;
        }
    }

    public void Dispose() { }
}

/// <summary>Collection definition with fixture to ensure headless platform
/// is initialized before any test runs.</summary>
[CollectionDefinition("AvaloniaHeadless")]
public class AvaloniaHeadlessTestCollection : ICollectionFixture<AvaloniaHeadlessFixture> { }
