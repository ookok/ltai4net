using Avalonia.Controls;
using LTAI.Desktop;

namespace LTAI.Desktop.Tests;

public sealed class ChatMessageRendererTests
{
    // ─── CleanResponse ───

    [Fact]
    public void CleanResponse_PassesThrough()
    {
        var result = ChatMessageRenderer.CleanResponse("hello world");
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void CleanResponse_EmptyString()
    {
        var result = ChatMessageRenderer.CleanResponse("");
        Assert.Equal("", result);
    }

    // ─── IsDiffContent ───

    [Fact]
    public void IsDiffContent_ReturnsTrue_ForDiffWithHeaders()
    {
        var diff = "--- a/file.cs\n+++ b/file.cs\n@@ -1,4 +1,5 @@\n-context";
        Assert.True(ChatMessageRenderer.IsDiffContent(diff));
    }

    [Fact]
    public void IsDiffContent_ReturnsFalse_ForPlainText()
    {
        Assert.False(ChatMessageRenderer.IsDiffContent("hello world"));
    }

    [Fact]
    public void IsDiffContent_ReturnsFalse_ForSingleMarker()
    {
        Assert.False(ChatMessageRenderer.IsDiffContent("--- a/file.cs\nno other markers"));
    }

    [Fact]
    public void IsDiffContent_ReturnsFalse_ForEmptyString()
    {
        Assert.False(ChatMessageRenderer.IsDiffContent(""));
    }

    // ─── SplitCodeBlocks ───

    [Fact]
    public void SplitCodeBlocks_PlainText_ReturnsSinglePart()
    {
        var parts = ChatMessageRenderer.SplitCodeBlocks("hello world");
        Assert.Single(parts);
        Assert.False(parts[0].IsCode);
        Assert.Equal("hello world", parts[0].Content);
    }

    [Fact]
    public void SplitCodeBlocks_SingleCodeBlock()
    {
        var text = "before\n```\ncode here\n```\nafter";
        var parts = ChatMessageRenderer.SplitCodeBlocks(text);
        Assert.Equal(3, parts.Count);
        Assert.False(parts[0].IsCode);
        Assert.True(parts[1].IsCode);
        Assert.Equal("code here", parts[1].Content);
        Assert.False(parts[2].IsCode);
    }

    [Fact]
    public void SplitCodeBlocks_EmptyInput()
    {
        var parts = ChatMessageRenderer.SplitCodeBlocks("");
        Assert.Empty(parts);
    }

    [Fact]
    public void SplitCodeBlocks_OnlyWhitespace_ReturnsEmpty()
    {
        var parts = ChatMessageRenderer.SplitCodeBlocks("   \n  \n  ");
        Assert.Empty(parts);
    }

    [Fact]
    public void SplitCodeBlocks_WithFenceAndMoreText_Works()
    {
        var text = "a\n```\ncode\n```\nb";
        var parts = ChatMessageRenderer.SplitCodeBlocks(text);
        Assert.NotEmpty(parts);
    }

    [Fact]
    public void SplitCodeBlocks_ConsecutiveFences()
    {
        var text = "```\nblock1\n```\n```\nblock2\n```";
        var parts = ChatMessageRenderer.SplitCodeBlocks(text);
        Assert.Equal(2, parts.Count);
        Assert.True(parts[0].IsCode);
        Assert.Equal("block1", parts[0].Content);
        Assert.True(parts[1].IsCode);
        Assert.Equal("block2", parts[1].Content);
    }

    // ─── TruncateFilePreview ───

    [Fact]
    public void TruncateFilePreview_ShortContent_ReturnsAsIs()
    {
        var content = "line1\nline2\nline3";
        var result = ChatMessageRenderer.TruncateFilePreview(content, "test.cs", 10);
        Assert.Equal(content, result);
    }

    [Fact]
    public void TruncateFilePreview_LongContent_Truncates()
    {
        var content = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line{i}"));
        var result = ChatMessageRenderer.TruncateFilePreview(content, "test.cs", 5);
        Assert.Contains("15 more lines", result);
        Assert.Contains("line5", result);
        Assert.DoesNotContain("line6", result);
    }

    [Fact]
    public void TruncateFilePreview_ExactMax_ReturnsAsIs()
    {
        var content = string.Join("\n", Enumerable.Range(1, 5).Select(i => $"line{i}"));
        var result = ChatMessageRenderer.TruncateFilePreview(content, "test.cs", 5);
        Assert.Equal(content, result);
    }

    [Fact]
    public void TruncateFilePreview_EmptyContent()
    {
        var result = ChatMessageRenderer.TruncateFilePreview("", "empty.cs");
        Assert.Equal("", result);
    }

    // ─── MakeCitationChip ───

    [Fact]
    public void MakeCitationChip_WithLine_CreatesBorder()
    {
        var chip = ChatMessageRenderer.MakeCitationChip("File.cs", 42, null);
        Assert.NotNull(chip);
        Assert.Contains("📄 File.cs:42", chip.Child switch
        {
            TextBlock tb => tb.Text,
            _ => ""
        });
    }

    [Fact]
    public void MakeCitationChip_WithoutLine_CreatesBorder()
    {
        var chip = ChatMessageRenderer.MakeCitationChip("File.cs", 0, null);
        Assert.NotNull(chip);
        Assert.Contains("📄 File.cs", chip.Child switch
        {
            TextBlock tb => tb.Text,
            _ => ""
        });
    }

    [Fact]
    public void MakeCitationChip_WithResolvedPath_SetsCursor()
    {
        var chip = ChatMessageRenderer.MakeCitationChip("File.cs", 1, "/tmp/File.cs");
        Assert.NotNull(chip.Cursor);
    }

    [Fact]
    public void MakeCitationChip_WithoutResolvedPath_NullCursor()
    {
        var chip = ChatMessageRenderer.MakeCitationChip("File.cs", 1, null);
        Assert.Null(chip.Cursor);
    }

    // ─── CopyButton ───

    [Fact]
    public void CopyButton_CreatesButton()
    {
        var btn = ChatMessageRenderer.CopyButton("hello");
        Assert.NotNull(btn);
        Assert.Equal("Copy", btn.Content?.ToString());
    }

    [Fact]
    public void CopyButton_HasCorrectSize()
    {
        var btn = ChatMessageRenderer.CopyButton("test");
        Assert.Equal(48, btn.Width);
        Assert.Equal(22, btn.Height);
    }

    // ─── RenderResponse (basic smoke test) ───

    [Fact]
    public void RenderResponse_EmptyPanel_DoesNotThrow()
    {
        var panel = new StackPanel();
        var ex = Record.Exception(() => ChatMessageRenderer.RenderResponse(panel, "hello"));
        Assert.Null(ex);
    }

    [Fact]
    public void RenderResponse_WithCodeBlock_AddsChildren()
    {
        var panel = new StackPanel();
        ChatMessageRenderer.RenderResponse(panel, "text\n```\ncode\n```\nmore");
        Assert.NotEmpty(panel.Children);
    }

    [Fact]
    public void RenderResponse_DiffContent_RendersDiff()
    {
        var panel = new StackPanel();
        var diff = "--- a/f.cs\n+++ b/f.cs\n@@ -1 +1 @@\n-old\n+new";
        ChatMessageRenderer.RenderResponse(panel, diff);
        Assert.NotEmpty(panel.Children);
    }

    [Fact]
    public void RenderResponse_EmptyText_NoChildren()
    {
        var panel = new StackPanel();
        ChatMessageRenderer.RenderResponse(panel, "");
        Assert.Empty(panel.Children);
    }

    [Fact]
    public void RenderDiffBlock_CreatesBorder()
    {
        var panel = new StackPanel();
        ChatMessageRenderer.RenderDiffBlock(panel, "--- a/f\n+++ b/f\n@@ -1 +1 @@\n-old\n+new");
        Assert.Single(panel.Children);
        Assert.IsType<Border>(panel.Children[0]);
    }

    // ─── UpdateResponseText ───

    [Fact]
    public void UpdateResponseText_AppendsWhenNull()
    {
        var panel = new StackPanel();
        TextBlock? current = null;
        ChatMessageRenderer.UpdateResponseText(panel, ref current, "hello");
        Assert.NotNull(current);
        Assert.Equal("hello", current.Text);
        Assert.Single(panel.Children);
    }

    [Fact]
    public void UpdateResponseText_UpdatesWhenChanged()
    {
        var panel = new StackPanel();
        TextBlock? current = null;
        ChatMessageRenderer.UpdateResponseText(panel, ref current, "hello");
        ChatMessageRenderer.UpdateResponseText(panel, ref current, "world");
        Assert.Equal("world", current!.Text);
    }

    [Fact]
    public void UpdateResponseText_DoesNotDuplicateWhenSame()
    {
        var panel = new StackPanel();
        TextBlock? current = null;
        ChatMessageRenderer.UpdateResponseText(panel, ref current, "hello");
        ChatMessageRenderer.UpdateResponseText(panel, ref current, "hello");
        Assert.Single(panel.Children);
    }
}
