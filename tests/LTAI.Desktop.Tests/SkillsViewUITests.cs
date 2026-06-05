using System.Reflection;
using Avalonia.Controls;
using Avalonia.Media;

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
        Assert.Contains("技能管理", header.Text);
    }

    [Fact]
    public void Constructor_NonexistentDir_ShowsWarning()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ltai_test_nonexistent_" + Guid.NewGuid().ToString("N"));
        var view = new SkillsView(dir);
        var contentField = typeof(SkillsView).GetField("_content",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tb = (TextBlock)contentField.GetValue(view)!;
        Assert.Contains("不存在", tb.Text);
        Assert.Contains(dir, tb.Text);
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
    public void GetDesc_WithDescription_ReturnsValue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ltai_test_desc_" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(dir, "test-skill");
        Directory.CreateDirectory(skillDir);
        var md = Path.Combine(skillDir, "SKILL.md");
        File.WriteAllText(md, "---\ndescription: \"A test skill\"\n---\n# Content");
        try
        {
            var method = typeof(SkillsView).GetMethod("GetDesc",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            var desc = (string)method.Invoke(null, [md])!;
            Assert.Equal("A test skill", desc);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void GetDesc_WithoutYaml_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ltai_test_noyaml_" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(dir, "no-yaml");
        Directory.CreateDirectory(skillDir);
        var md = Path.Combine(skillDir, "SKILL.md");
        File.WriteAllText(md, "# Just content\nno frontmatter");
        try
        {
            var method = typeof(SkillsView).GetMethod("GetDesc",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            var desc = (string)method.Invoke(null, [md])!;
            Assert.Equal("", desc);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LoadUsage_WithNoFile_ReturnsEmpty()
    {
        var method = typeof(SkillsView).GetMethod("LoadUsage",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var view = new SkillsView();
        var usage = method.Invoke(view, null);
        Assert.NotNull(usage);
    }

    [Fact]
    public void ContentText_Defaults_WhenDirMissing()
    {
        var view = new SkillsView("/nonexistent/path/for/skills");
        var contentField = typeof(SkillsView).GetField("_content",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tb = (TextBlock)contentField.GetValue(view)!;
        Assert.False(string.IsNullOrEmpty(tb.Text));
    }

    [Fact]
    public void SkillsView_ImplementsUserControl()
    {
        var view = new SkillsView();
        Assert.IsAssignableFrom<UserControl>(view);
    }
}
