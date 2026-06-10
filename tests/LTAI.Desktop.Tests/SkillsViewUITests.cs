using Avalonia.Controls;

namespace LTAI.Desktop.Tests;

public sealed class SkillsViewUITests
{
    [Fact]
    public void Constructor_HasContent()
    {
        var view = new SkillsView();
        Assert.NotNull(view.Content);
    }

    [Fact]
    public void Constructor_HasHeaderText()
    {
        var view = new SkillsView();
        var root = (StackPanel)view.Content!;
        var header = root.Children[0] as TextBlock;
        Assert.NotNull(header);
        Assert.Contains("技能", header.Text);
    }

    [Fact]
    public void Constructor_WithTempDir_DoesNotThrow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ltai_test_skills_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ex = Record.Exception(() => new SkillsView(dir));
            Assert.Null(ex);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SkillsView_ImplementsUserControl()
    {
        var view = new SkillsView();
        Assert.IsAssignableFrom<UserControl>(view);
    }
}
