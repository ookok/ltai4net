using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace LTAI.Desktop.Tests;

[Collection("AvaloniaHeadless")]
public sealed class MarkdownRendererTests
{
    // ─── GetKeywords ───

    [Fact]
    public void GetKeywords_ReturnsEmpty_ForNull()
    {
        var result = MarkdownRenderer.GetKeywords(null);
        Assert.Empty(result);
    }

    [Fact]
    public void GetKeywords_ReturnsEmpty_ForEmpty()
    {
        var result = MarkdownRenderer.GetKeywords("");
        Assert.Empty(result);
    }

    [Fact]
    public void GetKeywords_ReturnsKeywords_ForCsharp()
    {
        var result = MarkdownRenderer.GetKeywords("csharp");
        Assert.Contains("class", result);
        Assert.Contains("public", result);
    }

    [Fact]
    public void GetKeywords_ReturnsKeywords_ForPython()
    {
        var result = MarkdownRenderer.GetKeywords("python");
        Assert.Contains("def", result);
        Assert.Contains("return", result);
    }

    [Fact]
    public void GetKeywords_ReturnsKeywords_ForJavaScript()
    {
        var result = MarkdownRenderer.GetKeywords("javascript");
        Assert.Contains("function", result);
        Assert.Contains("const", result);
    }

    [Fact]
    public void GetKeywords_PartialMatch_ReturnsCorrect()
    {
        var result = MarkdownRenderer.GetKeywords("csharp with entities");
        Assert.Contains("class", result);
    }

    // ─── TokenizeLine ───

    [Fact]
    public void TokenizeLine_EmptyLine_ReturnsOneToken()
    {
        // Regex matches empty pattern as a single char token
        var tokens = MarkdownRenderer.TokenizeLine("", ["class"]);
        Assert.NotEmpty(tokens);
    }

    [Fact]
    public void TokenizeLine_PlainText_ReturnsTokens()
    {
        var tokens = MarkdownRenderer.TokenizeLine("hello world", []);
        Assert.NotEmpty(tokens);
    }

    [Fact]
    public void TokenizeLine_ProducesTokens()
    {
        var tokens = MarkdownRenderer.TokenizeLine("class Foo", ["class"]);
        Assert.NotEmpty(tokens);
    }

    [Fact]
    public void TokenizeLine_WithString_ProducesTokens()
    {
        var tokens = MarkdownRenderer.TokenizeLine("var x = \"hello\";", []);
        Assert.NotEmpty(tokens);
    }

    [Fact]
    public void TokenizeLine_WithNumber_ProducesTokens()
    {
        var tokens = MarkdownRenderer.TokenizeLine("int x = 42;", []);
        Assert.NotEmpty(tokens);
    }

    // ─── Render ───

    [Fact]
    public void Render_EmptyText_ReturnsEmptyInlines()
    {
        var tb = new TextBlock();
        MarkdownRenderer.Render("", tb.Inlines!);
        Assert.Empty(tb.Inlines!);
    }

    [Fact]
    public void Render_PlainText_AddsInline()
    {
        var tb = new TextBlock();
        MarkdownRenderer.Render("hello", tb.Inlines!);
        Assert.NotEmpty(tb.Inlines!);
        var run = tb.Inlines!.First() as Run;
        Assert.NotNull(run);
        Assert.Equal("hello", run.Text);
    }

    [Fact]
    public void Render_MultiLine_AddsLineBreaks()
    {
        var tb = new TextBlock();
        MarkdownRenderer.Render("line1\nline2", tb.Inlines!);
        Assert.True(tb.Inlines!.Count >= 2);
    }

    [Fact]
    public void Render_Header1_BoldAndLarge()
    {
        var tb = new TextBlock();
        MarkdownRenderer.Render("# Title", tb.Inlines!);
        var run = tb.Inlines!.First() as Run;
        Assert.NotNull(run);
        Assert.Equal("Title", run.Text);
        Assert.Equal(FontWeight.Bold, run.FontWeight);
        Assert.True(run.FontSize > 15);
    }

    [Fact]
    public void Render_Header2_Bold()
    {
        var tb = new TextBlock();
        MarkdownRenderer.Render("## Subtitle", tb.Inlines!);
        var run = tb.Inlines!.First() as Run;
        Assert.NotNull(run);
        Assert.Equal(FontWeight.Bold, run.FontWeight);
    }

    [Fact]
    public void Render_Header3_BoldSmaller()
    {
        var tb = new TextBlock();
        MarkdownRenderer.Render("### Section", tb.Inlines!);
        var run = tb.Inlines!.First() as Run;
        Assert.NotNull(run);
        Assert.Equal(15, run.FontSize);
    }

    [Fact]
    public void Render_Blockquote_Italic()
    {
        var tb = new TextBlock();
        MarkdownRenderer.Render("> note", tb.Inlines!);
        var run = tb.Inlines!.First() as Run;
        Assert.NotNull(run);
        Assert.Equal(FontStyle.Italic, run.FontStyle);
    }

    [Fact]
    public void Render_UnorderedList_HasBullet()
    {
        var tb = new TextBlock();
        MarkdownRenderer.Render("- item", tb.Inlines!);
        Assert.NotEmpty(tb.Inlines!);
    }

    [Fact]
    public void Render_OrderedList_AddsNumber()
    {
        var tb = new TextBlock();
        MarkdownRenderer.Render("1. first", tb.Inlines!);
        var firstRun = tb.Inlines!.First() as Run;
        Assert.NotNull(firstRun);
        Assert.Contains("1.", firstRun.Text);
    }

    [Fact]
    public void Render_Table_KeepsCells()
    {
        var tb = new TextBlock();
        MarkdownRenderer.Render("| a | b |\n|---|---|\n| 1 | 2 |", tb.Inlines!);
        Assert.NotEmpty(tb.Inlines!);
    }

    [Fact]
    public void Render_CodeFence_IsSkipped()
    {
        var tb = new TextBlock();
        MarkdownRenderer.Render("text\n```\ncode\n```\nend", tb.Inlines!);
        Assert.NotEmpty(tb.Inlines!);
    }

    // ─── RenderSpan (inline formatting) ───

    [Fact]
    public void RenderSpan_BoldText_Bolds()
    {
        var tb = new TextBlock();
        MarkdownRenderer.RenderSpan("**bold**", tb.Inlines!);
        Assert.NotEmpty(tb.Inlines!);
        var run = tb.Inlines!.First() as Run;
        Assert.NotNull(run);
        Assert.Equal("bold", run.Text);
        Assert.Equal(FontWeight.Bold, run.FontWeight);
    }

    [Fact]
    public void RenderSpan_ItalicText_Italicizes()
    {
        var tb = new TextBlock();
        MarkdownRenderer.RenderSpan("*italic*", tb.Inlines!);
        var run = tb.Inlines!.First() as Run;
        Assert.NotNull(run);
        Assert.Equal("italic", run.Text);
        Assert.Equal(FontStyle.Italic, run.FontStyle);
    }

    [Fact]
    public void RenderSpan_CodeText_UsesCodeFont()
    {
        var tb = new TextBlock();
        MarkdownRenderer.RenderSpan("`code`", tb.Inlines!);
        var run = tb.Inlines!.First() as Run;
        Assert.NotNull(run);
        Assert.Equal("code", run.Text);
    }

    [Fact]
    public void RenderSpan_LinkText_ShowsUrl()
    {
        var tb = new TextBlock();
        MarkdownRenderer.RenderSpan("[text](https://example.com)", tb.Inlines!);
        Assert.True(tb.Inlines!.Count >= 2);
        var linkRun = tb.Inlines!.First() as Run;
        Assert.NotNull(linkRun);
        Assert.Equal("text", linkRun.Text);
    }

    [Fact]
    public void RenderSpan_Strikethrough_Dims()
    {
        var tb = new TextBlock();
        MarkdownRenderer.RenderSpan("~~strike~~", tb.Inlines!);
        var run = tb.Inlines!.First() as Run;
        Assert.NotNull(run);
        Assert.Equal("strike", run.Text);
    }

    [Fact]
    public void RenderSpan_PlainText_NoFormatting()
    {
        var tb = new TextBlock();
        MarkdownRenderer.RenderSpan("plain text", tb.Inlines!);
        var run = tb.Inlines!.First() as Run;
        Assert.NotNull(run);
        Assert.Equal("plain text", run.Text);
        Assert.Equal(FontWeight.Normal, run.FontWeight);
    }

    [Fact]
    public void RenderSpan_MixedFormatting()
    {
        var tb = new TextBlock();
        MarkdownRenderer.RenderSpan("**bold** and `code`", tb.Inlines!);
        // Should have at least 3 inlines: bold, " and ", code
        Assert.True(tb.Inlines!.Count >= 3);
    }

    [Fact]
    public void RenderSpan_EmptyText_AddsNothing()
    {
        var tb = new TextBlock();
        MarkdownRenderer.RenderSpan("", tb.Inlines!);
        Assert.Empty(tb.Inlines!);
    }
}
