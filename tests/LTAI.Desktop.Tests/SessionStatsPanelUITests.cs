using LTAI.Core.Session;

namespace LTAI.Desktop.Tests;

public sealed class SessionStatsPanelUITests 
{
    private static SessionManager CreateSessionManager()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ltai_test_sessions_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new SessionManager(dir);
    }

    [Fact]
    public void Constructor_TakesSessionManager()
    {
        var mgr = CreateSessionManager();
        var panel = new SessionStatsPanel(mgr);
        Assert.NotNull(panel.Content);
    }

    [Fact]
    public void Constructor_HasContent()
    {
        var mgr = CreateSessionManager();
        var panel = new SessionStatsPanel(mgr);
        Assert.NotNull(panel.Content);
    }
}
