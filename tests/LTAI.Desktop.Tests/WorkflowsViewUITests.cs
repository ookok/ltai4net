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
    public void Constructor_DoesNotThrow()
    {
        var ex = Record.Exception(() => new WorkflowsView(null!));
        Assert.Null(ex);
    }
}
