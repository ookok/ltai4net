namespace LTAI.Desktop.Tests;

/// <summary>
/// Test App that inherits from real App (loads App.axaml styles via Initialize)
/// but skips DI initialization and MainWindow creation in OnFrameworkInitializationCompleted.
/// </summary>
public class TestApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        // Intentionally NOT calling base — the real App.OnFrameworkInitializationCompleted
        // starts Program.InitializeServicesAsync() and creates MainWindow.
        // For headless tests we create windows manually.
    }
}
