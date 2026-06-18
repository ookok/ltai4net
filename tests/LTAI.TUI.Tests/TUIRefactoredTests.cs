using System.Text;

namespace LTAI.TUI.Tests;

public sealed class InputHistoryTests
{
    [Fact]
    public void Empty_History_PreviousReturnsNull()
    {
        var h = new InputHistory();
        Assert.Null(h.Previous());
        Assert.Null(h.Next());
    }

    [Fact]
    public void Add_SingleItem_PreviousReturnsIt()
    {
        var h = new InputHistory();
        h.Add("hello");
        Assert.Equal("hello", h.Previous());
    }

    [Fact]
    public void Add_MultipleItems_NavigatesInOrder()
    {
        var h = new InputHistory();
        h.Add("first");
        h.Add("second");
        h.Add("third");

        Assert.Equal("third", h.Previous());
        Assert.Equal("second", h.Previous());
        Assert.Equal("first", h.Previous());
        Assert.Equal("first", h.Previous()); // clamped
    }

    [Fact]
    public void Next_AfterPrevious_ReturnsForward()
    {
        var h = new InputHistory();
        h.Add("a");
        h.Add("b");
        h.Add("c");

        Assert.Equal("c", h.Previous());
        Assert.Equal("b", h.Previous());
        Assert.Equal("c", h.Next());
        Assert.Null(h.Next()); // past the end
    }

    [Fact]
    public void Add_DuplicateConsecutive_NotStored()
    {
        var h = new InputHistory();
        h.Add("hello");
        h.Add("hello");
        Assert.Equal(1, h.Count);
    }

    [Fact]
    public void Add_EmptyString_NotStored()
    {
        var h = new InputHistory();
        h.Add("");
        h.Add(null!);
        Assert.Equal(0, h.Count);
    }

    [Fact]
    public void MaxCapacity_Enforced()
    {
        var h = new InputHistory(3);
        h.Add("a"); h.Add("b"); h.Add("c"); h.Add("d");
        Assert.Equal(3, h.Count);
        Assert.Equal("d", h.Previous());
        Assert.Equal("c", h.Previous());
        Assert.Equal("b", h.Previous());
    }

    [Fact]
    public void ResetIndex_AfterNavigation()
    {
        var h = new InputHistory();
        h.Add("x"); h.Add("y");
        Assert.Equal("y", h.Previous());
        h.ResetIndex();
        Assert.Null(h.Next()); // past end after reset
        Assert.Equal("y", h.Previous()); // fresh navigation
    }
}

public sealed class CommandHandlerTests
{
    private readonly List<string> _msgs = new();
    private readonly List<string> _conv = new();
    private readonly StringBuilder _cache = new();

    private CommandHandler CreateHandler()
    {
        return new CommandHandler(
            _conv, _cache, -1, "test-model", null,
            getActiveInput: () => "",
            addMsg: (role, msg) => _msgs.Add($"{role}: {msg}"),
            cancelStream: () => { },
            showSessionPicker: () => _msgs.Add("sessionpicker"),
            showSearchDialog: () => _msgs.Add("searchdialog"),
            handleModelCommand: () => _msgs.Add("modelcommand"),
            handleToolCommand: () => _msgs.Add("toolcommand"),
            requestStop: () => _msgs.Add("requeststop"));
    }

    [Fact]
    public void Command_New_ClearsState()
    {
        _conv.Add("existing message");
        _cache.Append("some cached text");
        var h = CreateHandler();
        Assert.True(h.Execute("new"));
        Assert.Empty(_conv);
        Assert.Empty(_cache.ToString());
    }

    [Fact]
    public void Command_Clear_ClearsState()
    {
        _conv.Add("existing");
        var h = CreateHandler();
        Assert.True(h.Execute("clear"));
        Assert.Empty(_conv);
    }

    [Fact]
    public void Command_Status_AddsSystemMessage()
    {
        var h = CreateHandler();
        h.Execute("status");
        Assert.Single(_msgs);
        Assert.StartsWith("System: **状态**", _msgs[0]);
    }

    [Fact]
    public void Command_Commands_AddsSystemMessage()
    {
        var h = CreateHandler();
        h.Execute("commands");
        Assert.Single(_msgs);
        Assert.Contains("/model", _msgs[0]);
    }

    [Fact]
    public void Command_Help_AddsSystemMessage()
    {
        var h = CreateHandler();
        h.Execute("help");
        Assert.Single(_msgs);
        Assert.Contains("/commands", _msgs[0]);
    }

    [Fact]
    public void Command_Exit_CallsRequestStop()
    {
        var h = CreateHandler();
        h.Execute("exit");
        Assert.Contains(_msgs, m => m == "requeststop");
    }

    [Fact]
    public void Command_Sessions_CallsShowSessionPicker()
    {
        var h = CreateHandler();
        h.Execute("sessions");
        Assert.Contains(_msgs, m => m == "sessionpicker");
    }

    [Fact]
    public void Command_Search_CallsShowSearchDialog()
    {
        var h = CreateHandler();
        h.Execute("search");
        Assert.Contains(_msgs, m => m == "searchdialog");
    }

    [Fact]
    public void Command_Model_CallsHandleModelCommand()
    {
        var h = CreateHandler();
        h.Execute("model");
        Assert.Contains(_msgs, m => m == "modelcommand");
    }

    [Fact]
    public void Command_Tool_CallsHandleToolCommand()
    {
        var h = CreateHandler();
        h.Execute("tool");
        Assert.Contains(_msgs, m => m == "toolcommand");
    }

    [Fact]
    public void Command_Retry_AddsSystemMessage()
    {
        var h = CreateHandler();
        h.Execute("retry");
        Assert.Single(_msgs);
        Assert.Contains("重发暂未实现", _msgs[0]);
    }

    [Fact]
    public void Command_Savings_AddsSystemMessage()
    {
        var h = CreateHandler();
        h.Execute("savings");
        Assert.Single(_msgs);
        Assert.Contains("Token", _msgs[0]);
    }

    [Fact]
    public void Command_Unknown_ReturnsFalse()
    {
        var h = CreateHandler();
        Assert.False(h.Execute("nonexistent"));
    }

    [Fact]
    public void Command_Theme_AddsMessage()
    {
        var h = CreateHandler();
        h.Execute("theme");
        Assert.Single(_msgs);
        Assert.Contains("主题", _msgs[0]);
    }
}
