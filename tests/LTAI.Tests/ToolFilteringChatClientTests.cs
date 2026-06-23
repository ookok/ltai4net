using LTAI.AI;
using LTAI.Agent.Clients;
using Microsoft.Extensions.AI;
using Xunit;

namespace LTAI.Tests;

public sealed class ToolFilteringChatClientTests
{
    // ═══════════════════════════════════════════════
    //  GetLastUserQuery (static pure method)
    // ═══════════════════════════════════════════════

    [Fact]
    public void GetLastUserQuery_EmptyMessages_ReturnsEmpty()
    {
        var result = InvokeGetLastUserQuery([]);
        Assert.Equal("", result);
    }

    [Fact]
    public void GetLastUserQuery_SingleUserMessage_ReturnsText()
    {
        var msgs = new List<ChatMessage>
        {
            new(ChatRole.User, "hello world"),
        };
        var result = InvokeGetLastUserQuery(msgs);
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void GetLastUserQuery_TwoUserMessages_ReturnsCombined()
    {
        var msgs = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "hi"),
            new(ChatRole.User, "first query"),
            new(ChatRole.Assistant, "response"),
            new(ChatRole.User, "second query"),
        };
        var result = InvokeGetLastUserQuery(msgs);
        Assert.Equal("first query second query", result);
    }

    [Fact]
    public void GetLastUserQuery_WhitespaceOnly_ReturnsEmpty()
    {
        var msgs = new List<ChatMessage>
        {
            new(ChatRole.User, "   "),
        };
        var result = InvokeGetLastUserQuery(msgs);
        Assert.Equal("", result);
    }

    [Fact]
    public void GetLastUserQuery_OnlyAssistant_ReturnsEmpty()
    {
        var msgs = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "response"),
        };
        var result = InvokeGetLastUserQuery(msgs);
        Assert.Equal("", result);
    }

    // ═══════════════════════════════════════════════
    //  ParseToolNames (static pure method)
    // ═══════════════════════════════════════════════

    [Fact]
    public void ParseToolNames_EmptyInput_ReturnsEmpty()
    {
        var result = InvokeParseToolNames("");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseToolNames_SingleName_ReturnsIt()
    {
        var result = InvokeParseToolNames("ReadFileContent");
        Assert.Contains("ReadFileContent", result);
    }

    [Fact]
    public void ParseToolNames_MultipleNames_ParsesCorrectly()
    {
        var result = InvokeParseToolNames("ReadFileContent\nGlob\nRunCommand");
        Assert.Equal(3, result.Count);
        Assert.Contains("ReadFileContent", result);
        Assert.Contains("Glob", result);
        Assert.Contains("RunCommand", result);
    }

    [Fact]
    public void ParseToolNames_WithBulletPrefixes_StripsThem()
    {
        var result = InvokeParseToolNames("- ReadFileContent\n* Glob\n1. RunCommand");
        Assert.Equal(3, result.Count);
        Assert.Contains("ReadFileContent", result);
        Assert.Contains("Glob", result);
        Assert.Contains("RunCommand", result);
    }

    [Fact]
    public void ParseToolNames_WithSpacesExcludesThem()
    {
        var result = InvokeParseToolNames("ReadFileContent\nSome Tool With Spaces");
        Assert.Single(result);
        Assert.Contains("ReadFileContent", result);
    }

    [Fact]
    public void ParseToolNames_WithColonExcludesThem()
    {
        var result = InvokeParseToolNames("ReadFileContent\nNote: description");
        Assert.Single(result);
    }

    // ═══════════════════════════════════════════════
    //  FilterToolsAsync — pinned tools behavior
    // ═══════════════════════════════════════════════

    [Fact]
    public async Task FilterToolsAsync_NullOptions_ReturnsNull()
    {
        var client = CreateClient();
        var msgs = new List<ChatMessage> { new(ChatRole.User, "test") };

        var result = await client.GetResponseAsync(msgs, null);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task FilterToolsAsync_EmptyTools_ReturnsUnchanged()
    {
        var client = CreateClient();
        var opts = new ChatOptions { Tools = [] };
        var msgs = new List<ChatMessage> { new(ChatRole.User, "test") };

        var result = await client.GetResponseAsync(msgs, opts);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task FilterToolsAsync_EmptyQuery_UsesAllTools()
    {
        var client = CreateClient();
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(() => { }, "ReadFileContent", "Read a file"),
            AIFunctionFactory.Create(() => { }, "WriteFile", "Write a file"),
            AIFunctionFactory.Create(() => { }, "RunCommand", "Run a command"),
        };
        var opts = new ChatOptions { Tools = tools };
        var msgs = new List<ChatMessage>();

        var result = await client.GetResponseAsync(msgs, opts);

        Assert.NotNull(result);
    }

    [Fact]
    public void Ctor_NullInner_DoesNotThrow()
    {
        var ex = Record.Exception(() => new ToolFilteringChatClient(null!, null!, null!));
        Assert.Null(ex);
    }

    // ═══════════════════════════════════════════════
    //  Pinned tools set verification
    // ═══════════════════════════════════════════════

    [Fact]
    public void PinnedTools_ContainsReadFileContent()
    {
        var tools = GetPinnedTools();
        Assert.Contains("ReadFileContent", tools);
    }

    [Fact]
    public void PinnedTools_ContainsRunCommand()
    {
        var tools = GetPinnedTools();
        Assert.Contains("RunCommand", tools);
    }

    [Fact]
    public void PinnedTools_ContainsListFiles()
    {
        var tools = GetPinnedTools();
        Assert.Contains("ListFiles", tools);
    }

    [Fact]
    public void PinnedTools_ContainsGetCurrentDateTime()
    {
        var tools = GetPinnedTools();
        Assert.Contains("GetCurrentDateTime", tools);
    }

    [Fact]
    public void PinnedTools_CountIs4()
    {
        var tools = GetPinnedTools();
        Assert.Equal(4, tools.Count);
    }

    [Fact]
    public void PinnedTools_CaseInsensitive()
    {
        var tools = GetPinnedTools();
        Assert.Contains("readfilecontent", tools);
        Assert.Contains("READFILECONTENT", tools);
    }

    // ═══════════════════════════════════════════════
    //  Helpers — reflection-based invokers
    // ═══════════════════════════════════════════════

    private static string InvokeGetLastUserQuery(List<ChatMessage> messages)
    {
        var method = typeof(ToolFilteringChatClient).GetMethod("GetLastUserQuery",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, [messages])!;
    }

    private static List<string> InvokeParseToolNames(string input)
    {
        var method = typeof(ToolFilteringChatClient).GetMethod("ParseToolNames",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (List<string>)method!.Invoke(null, [input])!;
    }

    private static HashSet<string> GetPinnedTools()
    {
        var field = typeof(ToolFilteringChatClient).GetField("PinnedTools",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (HashSet<string>)field!.GetValue(null)!;
    }

    private static ToolFilteringChatClient CreateClient()
    {
        var toolReg = new LTAI.AI.ToolRegistry();
        return new ToolFilteringChatClient(new EchoChatClient("ok"), null!, toolReg);
    }
}

file sealed class TestAITool(string name) : AITool
{
    public string ToolName => name;
}
