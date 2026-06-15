using LTAI.TUI.Rendering;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI.Tests;

public sealed class FramebufferRendererTests
{
    [Fact]
    public void Cell_Default_CharIsNull()
    {
        var cell = default(FramebufferRenderer.Cell);
        Assert.Equal('\0', cell.Char);
        Assert.Null(cell.Foreground);
        Assert.Null(cell.Background);
    }

    [Fact]
    public void Cell_Equals_SameValues_ReturnsTrue()
    {
        var a = new FramebufferRenderer.Cell { Char = 'A', Foreground = Color.Red, Background = Color.Blue };
        var b = new FramebufferRenderer.Cell { Char = 'A', Foreground = Color.Red, Background = Color.Blue };
        Assert.True(a.Equals(b));
    }

    [Fact]
    public void Cell_Equals_DifferentChar_ReturnsFalse()
    {
        var a = new FramebufferRenderer.Cell { Char = 'A' };
        var b = new FramebufferRenderer.Cell { Char = 'B' };
        Assert.False(a.Equals(b));
    }

    [Fact]
    public void Cell_Equals_DifferentForeground_ReturnsFalse()
    {
        var a = new FramebufferRenderer.Cell { Char = 'X', Foreground = Color.Red };
        var b = new FramebufferRenderer.Cell { Char = 'X', Foreground = Color.Green };
        Assert.False(a.Equals(b));
    }

    [Fact]
    public void Cell_Equals_DifferentBackground_ReturnsFalse()
    {
        var a = new FramebufferRenderer.Cell { Char = 'X', Background = Color.Blue };
        var b = new FramebufferRenderer.Cell { Char = 'X', Background = Color.Yellow };
        Assert.False(a.Equals(b));
    }

    [Fact]
    public void Cell_Equals_NullObject_ReturnsFalse()
    {
        var cell = new FramebufferRenderer.Cell { Char = 'A' };
        Assert.False(cell.Equals((object?)null));
    }

    [Fact]
    public void Cell_Equals_DifferentType_ReturnsFalse()
    {
        var cell = new FramebufferRenderer.Cell { Char = 'A' };
        Assert.False(cell.Equals("not a cell"));
    }

    [Fact]
    public void Cell_GetHashCode_SameValues_SameHash()
    {
        var a = new FramebufferRenderer.Cell { Char = 'Z', Foreground = Color.Red };
        var b = new FramebufferRenderer.Cell { Char = 'Z', Foreground = Color.Red };
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Cell_GetHashCode_DifferentValues_DifferentHash()
    {
        var a = new FramebufferRenderer.Cell { Char = 'A' };
        var b = new FramebufferRenderer.Cell { Char = 'B' };
        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Cell_WithNullColors_EqualsWorks()
    {
        var a = new FramebufferRenderer.Cell { Char = 'H', Foreground = null, Background = null };
        var b = new FramebufferRenderer.Cell { Char = 'H', Foreground = null, Background = null };
        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var ex = Record.Exception(() => new FramebufferRenderer());
        Assert.Null(ex);
    }

    [Fact]
    public void Constructor_CreatesDisposable()
    {
        using var renderer = new FramebufferRenderer();
        Assert.NotNull(renderer);
    }

    [Fact]
    public void Initialize_DoesNotThrow()
    {
        using var renderer = new FramebufferRenderer();
        var ex = Record.Exception(() => renderer.Initialize());
        Assert.Null(ex);
    }

    [Fact]
    public void Shutdown_DoesNotThrow()
    {
        using var renderer = new FramebufferRenderer();
        renderer.Initialize();
        var ex = Record.Exception(() => renderer.Shutdown());
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var renderer = new FramebufferRenderer();
        renderer.Dispose();
        var ex = Record.Exception(() => renderer.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void RenderAndFlush_EmptyMarkup_DoesNotThrow()
    {
        using var renderer = new FramebufferRenderer();
        var markup = new Markup("");
        var ex = Record.Exception(() => renderer.RenderAndFlush(markup));
        Assert.Null(ex);
    }

    [Fact]
    public void RenderAndFlush_PlainText_DoesNotThrow()
    {
        using var renderer = new FramebufferRenderer();
        var markup = new Markup("hello world");
        var ex = Record.Exception(() => renderer.RenderAndFlush(markup));
        Assert.Null(ex);
    }

    [Fact]
    public void RenderAndFlush_StyledText_DoesNotThrow()
    {
        using var renderer = new FramebufferRenderer();
        var markup = new Markup("[bold red]Important[/] [blue]info[/]");
        var ex = Record.Exception(() => renderer.RenderAndFlush(markup));
        Assert.Null(ex);
    }

    [Fact]
    public void RenderAndFlush_MultiLineText_DoesNotThrow()
    {
        using var renderer = new FramebufferRenderer();
        var markup = new Markup("line1\nline2\nline3");
        var ex = Record.Exception(() => renderer.RenderAndFlush(markup));
        Assert.Null(ex);
    }
}

public sealed class MainWindowSmokeTests
{
    [Fact]
    public void MainWindow_Type_IsPublic()
    {
        var t = typeof(MainWindow);
        Assert.True(t.IsPublic);
        Assert.True(t.IsSealed);
    }
}
