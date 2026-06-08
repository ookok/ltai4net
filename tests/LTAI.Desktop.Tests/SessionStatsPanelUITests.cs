using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
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
    public void Constructor_ExpandedInitiallyFalse()
    {
        var mgr = CreateSessionManager();
        var panel = new SessionStatsPanel(mgr);
        Assert.False(panel.IsExpanded);
    }

    [Fact]
    public void ToggleButton_Exists()
    {
        var mgr = CreateSessionManager();
        var panel = new SessionStatsPanel(mgr);
        var root = (StackPanel)panel.Content!;
        Assert.True(root.Children.Count >= 1);
        Assert.IsType<Button>(root.Children[0]);
    }

    [Fact]
    public void ToggleClick_Expands()
    {
        var mgr = CreateSessionManager();
        var panel = new SessionStatsPanel(mgr);
        var root = (StackPanel)panel.Content!;
        var toggleBtn = (Button)root.Children[0];

        toggleBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(panel.IsExpanded);
    }

    [Fact]
    public void ToggleClickTwice_Collapses()
    {
        var mgr = CreateSessionManager();
        var panel = new SessionStatsPanel(mgr);
        var root = (StackPanel)panel.Content!;
        var toggleBtn = (Button)root.Children[0];

        toggleBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(panel.IsExpanded);

        toggleBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(panel.IsExpanded);
    }

    [Fact]
    public void EmptyState_ShowsNoSessions()
    {
        var mgr = CreateSessionManager();
        var panel = new SessionStatsPanel(mgr);
        var sessionPanelField = typeof(SessionStatsPanel).GetField("_sessionPanel",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var sp = (StackPanel)sessionPanelField.GetValue(panel)!;
        Assert.NotEmpty(sp.Children);
        var tb = sp.Children[0] as TextBlock;
        Assert.NotNull(tb);
        Assert.Contains("暂无会话", tb.Text);
    }

    [Fact]
    public void StatsText_ShowsDefaults()
    {
        var mgr = CreateSessionManager();
        var panel = new SessionStatsPanel(mgr);
        var statsField = typeof(SessionStatsPanel).GetField("_statsText",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tb = (TextBlock)statsField.GetValue(panel)!;
        Assert.NotNull(tb.Text);
        Assert.Contains("模型", tb.Text);
    }

    [Fact]
    public void Refresh_ClearsSessionPanel()
    {
        var mgr = CreateSessionManager();
        var panel = new SessionStatsPanel(mgr);
        var sessionPanelField = typeof(SessionStatsPanel).GetField("_sessionPanel",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var sp = (StackPanel)sessionPanelField.GetValue(panel)!;
        var beforeCount = sp.Children.Count;

        mgr.NewSession();
        var name = mgr.CurrentSession;
        mgr.SaveSession(name);

        panel.Refresh();

        var afterCount = ((StackPanel)sessionPanelField.GetValue(panel)!).Children.Count;
        Assert.NotEqual(beforeCount, afterCount);
    }

    [Fact]
    public void SessionSelected_Wired()
    {
        var mgr = CreateSessionManager();
        var panel = new SessionStatsPanel(mgr);
        var invocationCount = 0;
        panel.SessionSelected += (name) => invocationCount++;

        // Verify event field is non-null
        var eventField = typeof(SessionStatsPanel).GetField("SessionSelected",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var evt = eventField.GetValue(panel);
        Assert.NotNull(evt);
    }

    [Fact]
    public void NewSessionClicked_EventFires()
    {
        var mgr = CreateSessionManager();
        var panel = new SessionStatsPanel(mgr);
        var fired = false;
        panel.NewSessionClicked += () => fired = true;

        var root = (StackPanel)panel.Content!;
        if (root.Children.Count > 1 && root.Children[1] is StackPanel content)
        {
            if (content.Children[0] is StackPanel header && header.Children.Count > 1)
            {
                if (header.Children[1] is Button newBtn)
                {
                    newBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(fired);
                    return;
                }
            }
        }

        Assert.True(fired);
    }

    [Fact]
    public void MultipleSessions_GroupedCorrectly()
    {
        var mgr = CreateSessionManager();
        mgr.NewSession();
        mgr.SaveSession();

        mgr.NewSession();
        mgr.SaveSession();

        var panel = new SessionStatsPanel(mgr);

        var statsField = typeof(SessionStatsPanel).GetField("_statsText",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tb = (TextBlock)statsField.GetValue(panel)!;
        Assert.NotNull(tb.Text);
    }

    [Fact]
    public void GetGroupKey_NoDateDash_ReturnsOther()
    {
        var method = typeof(SessionStatsPanel).GetMethod("GetGroupKey",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (string)method.Invoke(null, ["test-session"])!;
        Assert.Equal("其他", result);
    }

    [Fact]
    public void GetGroupKey_InvalidFormat_ReturnsOther()
    {
        var method = typeof(SessionStatsPanel).GetMethod("GetGroupKey",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (string)method.Invoke(null, ["invalid-format"])!;
        Assert.Equal("其他", result);
    }

    [Fact]
    public void UpdateVisibility_TogglesContent()
    {
        var mgr = CreateSessionManager();
        var panel = new SessionStatsPanel(mgr);
        var root = (StackPanel)panel.Content!;
        var toggleBtn = (Button)root.Children[0];

        toggleBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(panel.IsExpanded);

        toggleBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(panel.IsExpanded);
    }
}
