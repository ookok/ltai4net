using System.Reflection;
using Avalonia.Controls;
using Avalonia.Media;
using LTAI.Agent.Tools;

namespace LTAI.Desktop.Tests;

/// <summary>
/// Tests for JobsView. Uses reflection to verify internal state
/// without triggering Avalonia visual tree (avoids threading issues in headless mode).
/// </summary>
public sealed class JobsViewUITests : AvaloniaUITestBase
{
    private static LTAIService CreateMockSvc() => null!;

    [Fact]
    public void Constructor_SetsContent()
    {
        var view = new JobsView(CreateMockSvc());
        Assert.NotNull(view.Content);
    }

    [Fact]
    public void EmptyState_HasEmptyText()
    {
        var view = new JobsView(CreateMockSvc());
        var emptyField = typeof(JobsView).GetField("_emptyText", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tb = (TextBlock)emptyField.GetValue(view)!;
        Assert.Contains("暂无后台作业", tb.Text);
    }

    [Fact]
    public void Footer_DefaultsToDash()
    {
        var view = new JobsView(CreateMockSvc());
        var footerField = typeof(JobsView).GetField("_footerText", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tb = (TextBlock)footerField.GetValue(view)!;
        Assert.Equal("—", tb.Text);
    }

    [Fact]
    public void Refresh_ShowsJobs()
    {
        var view = new JobsView(CreateMockSvc());
        var bgjs = new BackgroundJobService();
        bgjs.StartJob("echo hello");

        var jobsField = typeof(JobsView).GetField("_jobs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        jobsField.SetValue(view, bgjs);

        var refreshMethod = typeof(JobsView).GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance)!;
        refreshMethod.Invoke(view, null);

        var footerField = typeof(JobsView).GetField("_footerText", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tb = (TextBlock)footerField.GetValue(view)!;
        Assert.Contains("共", tb.Text);
        Assert.Contains("作业", tb.Text);
    }

    [Fact]
    public void Refresh_WithMultipleJobs_ShowsFooterCount()
    {
        var view = new JobsView(CreateMockSvc());
        var bgjs = new BackgroundJobService();
        bgjs.StartJob("echo 1");
        bgjs.StartJob("echo 2");

        var jobsField = typeof(JobsView).GetField("_jobs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        jobsField.SetValue(view, bgjs);

        var refreshMethod = typeof(JobsView).GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance)!;
        refreshMethod.Invoke(view, null);

        var footerField = typeof(JobsView).GetField("_footerText", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tb = (TextBlock)footerField.GetValue(view)!;
        Assert.Contains("2", tb.Text);
    }

    [Fact]
    public void Refresh_HandlesNullJobService()
    {
        var view = new JobsView(CreateMockSvc());
        // _jobs is null by default

        var refreshMethod = typeof(JobsView).GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance)!;
        refreshMethod.Invoke(view, null);

        var footerField = typeof(JobsView).GetField("_footerText", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tb = (TextBlock)footerField.GetValue(view)!;
        Assert.Contains("not available", tb.Text);
    }

    [Fact]
    public void Refresh_WithCompletedJob_ShowsRunningCount()
    {
        var view = new JobsView(CreateMockSvc());
        var bgjs = new BackgroundJobService();
        bgjs.StartJob("echo done");

        var jobsField = typeof(JobsView).GetField("_jobs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        jobsField.SetValue(view, bgjs);

        // Mark as completed
        var snap = bgjs.SnapshotJobs();
        foreach (var (id, entry) in snap)
        {
            entry.Completed = true;
            entry.ExitCode = 0;
        }

        var refreshMethod = typeof(JobsView).GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance)!;
        refreshMethod.Invoke(view, null);

        var footerField = typeof(JobsView).GetField("_footerText", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tb = (TextBlock)footerField.GetValue(view)!;
        Assert.Contains("0 运行中", tb.Text);
    }

    [Fact]
    public void Constructor_HasHeaderRow()
    {
        var view = new JobsView(CreateMockSvc());
        var rowsField = typeof(JobsView).GetField("_rowsPanel", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var panel = (StackPanel)rowsField.GetValue(view)!;
        // Should have at least the empty state text
        Assert.NotEmpty(panel.Children);
    }

    [Fact]
    public void Refresh_SeenIds_TracksJobIds()
    {
        var view = new JobsView(CreateMockSvc());
        var bgjs = new BackgroundJobService();
        bgjs.StartJob("echo test");

        var jobsField = typeof(JobsView).GetField("_jobs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        jobsField.SetValue(view, bgjs);

        var seenField = typeof(JobsView).GetField("_seenIds", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var seen = (HashSet<string>)seenField.GetValue(view)!;
        Assert.Empty(seen);

        var refreshMethod = typeof(JobsView).GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance)!;
        refreshMethod.Invoke(view, null);

        Assert.NotEmpty(seen);
    }
}
