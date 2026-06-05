using System.Reflection;
using Avalonia.Controls;
using LTAI.Agent.Workflows;

namespace LTAI.Desktop.Tests;

public sealed class WorkflowsViewUITests : AvaloniaUITestBase
{
    [Fact]
    public void Constructor_HasContent()
    {
        var view = new WorkflowsView(null!);
        Assert.NotNull(view.Content);
    }

    [Fact]
    public void StatusText_DefaultsToDash()
    {
        var view = new WorkflowsView(null!);
        var field = typeof(WorkflowsView).GetField("_statusText", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tb = (TextBlock)field.GetValue(view)!;
        Assert.NotNull(tb);
        Assert.Equal("—", tb.Text);
    }

    [Fact]
    public void ErrorText_DefaultsToDash()
    {
        var view = new WorkflowsView(null!);
        var field = typeof(WorkflowsView).GetField("_errorText", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tb = (TextBlock)field.GetValue(view)!;
        Assert.NotNull(tb);
        Assert.Equal("—", tb.Text);
    }

    [Fact]
    public void LastReloadText_DefaultsToDash()
    {
        var view = new WorkflowsView(null!);
        var field = typeof(WorkflowsView).GetField("_lastReloadText", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tb = (TextBlock)field.GetValue(view)!;
        Assert.NotNull(tb);
        Assert.Equal("—", tb.Text);
    }

    [Fact]
    public void ReloadAllButton_Exists()
    {
        var view = new WorkflowsView(null!);
        var field = typeof(WorkflowsView).GetField("_reloadAllBtn", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var btn = (Button)field.GetValue(view)!;
        Assert.NotNull(btn);
        Assert.Contains("Reload All", btn.Content?.ToString());
    }

    [Fact]
    public void OpenDevUiButton_Exists()
    {
        var view = new WorkflowsView(null!);
        var field = typeof(WorkflowsView).GetField("_openDevUiBtn", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var btn = (Button)field.GetValue(view)!;
        Assert.NotNull(btn);
        Assert.Contains("DevUI", btn.Content?.ToString());
    }

    [Fact]
    public void DevUiStatusText_InitiallyHidden()
    {
        var view = new WorkflowsView(null!);
        var field = typeof(WorkflowsView).GetField("_devUiStatusText", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tb = (TextBlock)field.GetValue(view)!;
        Assert.False(tb.IsVisible);
    }

    [Fact]
    public void RefreshStatus_WithNullRegistry_DoesNotThrow()
    {
        var view = new WorkflowsView(null!);
        var refreshMethod = typeof(WorkflowsView).GetMethod("RefreshStatus", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var ex = Record.Exception(() => refreshMethod.Invoke(view, null));
        Assert.Null(ex);
    }

    [Fact]
    public void OnReloadedFromNotifier_DoesNotThrow()
    {
        var view = new WorkflowsView(null!);
        var evt = new WorkflowReloadEvent("test", "seq", 1, DateTime.UtcNow, "/tmp/test.yaml");
        var method = typeof(WorkflowsView).GetMethod("OnReloadedFromNotifier",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var ex = Record.Exception(() => method.Invoke(view, [evt]));
        Assert.Null(ex);
    }

    [Fact]
    public void OnLoadFailedFromNotifier_DoesNotThrow()
    {
        var view = new WorkflowsView(null!);
        var evt = new WorkflowLoadFailedEvent("test", "yaml", "/tmp/test.yaml", "parse error", DateTime.UtcNow);
        var method = typeof(WorkflowsView).GetMethod("OnLoadFailedFromNotifier",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var ex = Record.Exception(() => method.Invoke(view, [evt]));
        Assert.Null(ex);
    }
}
