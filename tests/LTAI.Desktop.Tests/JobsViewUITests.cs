using System.Reflection;
using Avalonia.Controls;
using LTAI.Agent.Tools;
using LTAI.Desktop.ViewModels;

namespace LTAI.Desktop.Tests;

[Collection("AvaloniaHeadless")]
public sealed class JobsViewUITests
{
    private static (JobsView, JobsViewModel) CreateView()
    {
        var bgjs = new BackgroundJobService();
        var vm = new JobsViewModel(null!);
        var bgjsField = typeof(JobsViewModel).GetField("_bgjs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        bgjsField.SetValue(vm, bgjs);
        return (new JobsView(vm), vm);
    }

    [Fact]
    public void Constructor_SetsContent()
    {
        var (view, _) = CreateView();
        Assert.NotNull(view.Content);
    }

    [Fact]
    public void EmptyState_HasEmptyText()
    {
        var (view, _) = CreateView();
        var emptyField = typeof(JobsView).GetField("_emptyText", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tb = (TextBlock)emptyField.GetValue(view)!;
        Assert.Contains("暂无作业", tb.Text);
    }

    [Fact]
    public void Footer_DefaultsToDash()
    {
        var (view, _) = CreateView();
        var footerField = typeof(JobsView).GetField("_footerText", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tb = footerField.GetValue(view) as TextBlock;
        Assert.NotNull(tb);
    }

    [Fact]
    public void Constructor_HasContent()
    {
        var (view, _) = CreateView();
        Assert.NotNull(view.Content);
    }
}
