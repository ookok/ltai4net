using Xunit;

namespace LTAI.TUI.Tests;

public sealed class ConfirmRequestParserTests
{
    [Fact]
    public void Parse_NullText_ReturnsNull()
    {
        Assert.Null(ConfirmRequestParser.Parse(null!));
    }

    [Fact]
    public void Parse_EmptyText_ReturnsNull()
    {
        Assert.Null(ConfirmRequestParser.Parse(""));
        Assert.Null(ConfirmRequestParser.Parse("   "));
    }

    [Fact]
    public void Parse_ShellCommand_ReturnsConfirmInfo()
    {
        var text = "⚠️ 需要执行 shell 命令，但尚未确认。\n命令: `dotnet build`\n目录: /project";
        var result = ConfirmRequestParser.Parse(text);
        Assert.NotNull(result);
        Assert.Equal("执行 Shell 命令", result.Value.title);
        Assert.Contains("dotnet build", result.Value.message);
    }

    [Fact]
    public void Parse_PathOutsideWorkspace_ReturnsConfirmInfo()
    {
        var text = "⚠️ 路径在工作区外: `C:/outside/file.txt`";
        var result = ConfirmRequestParser.Parse(text);
        Assert.NotNull(result);
        Assert.Equal("访问工作区外路径", result.Value.title);
        Assert.Contains("outside", result.Value.message);
    }

    [Fact]
    public void Parse_FileDownload_ReturnsConfirmInfo()
    {
        var text = "需要下载文件，请用户确认。地址: https://example.com/file.zip";
        var result = ConfirmRequestParser.Parse(text);
        Assert.NotNull(result);
        Assert.Equal("下载文件", result.Value.title);
    }

    [Fact]
    public void Parse_EnvVarSet_ReturnsConfirmInfo()
    {
        var text = "需要设置环境变量 API_KEY，请确认";
        var result = ConfirmRequestParser.Parse(text);
        Assert.NotNull(result);
        Assert.Equal("设置环境变量", result.Value.title);
    }

    [Fact]
    public void Parse_EditFileOutsideWorkspace_ReturnsConfirmInfo()
    {
        var text = "需要编辑工作区外的文件，目标路径: `C:/outside/file.cs`";
        var result = ConfirmRequestParser.Parse(text);
        Assert.NotNull(result);
        Assert.Equal("编辑文件", result.Value.title);
    }

    [Fact]
    public void Parse_GenericSafety_ReturnsConfirmInfo()
    {
        var text = "⚠️ 检测到敏感操作，请确认是否继续执行。\n详情：将要删除文件";
        var result = ConfirmRequestParser.Parse(text);
        Assert.NotNull(result);
        Assert.Equal("安全确认", result.Value.title);
    }

    [Fact]
    public void Parse_NormalText_ReturnsNull()
    {
        Assert.Null(ConfirmRequestParser.Parse("今天天气不错"));
        Assert.Null(ConfirmRequestParser.Parse("Hello, how are you?"));
        Assert.Null(ConfirmRequestParser.Parse("var x = 1;"));
    }
}
