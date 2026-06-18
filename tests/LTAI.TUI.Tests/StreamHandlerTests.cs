using System.Runtime.CompilerServices;
using System.Text;
using LTAI.Core.Session;
using Microsoft.Extensions.AI;

namespace LTAI.TUI.Tests;

public sealed class StreamHandlerTests
{
    private readonly List<string> _conv = new();
    private readonly StringBuilder _cache = new();
    private readonly List<string> _toolStatuses = new();
    private readonly List<string> _modifiedFiles = new();
    private readonly List<string> _spinnerStates = new();
    private bool _refreshed;

    /// <summary>Minimal IStreamSource that returns predefined text chunks.</summary>
    private sealed class FakeStreamSource : IStreamSource
    {
        private readonly List<string> _responses;
        public FakeStreamSource(params string[] responses) => _responses = responses.ToList();

        public async IAsyncEnumerable<Microsoft.Agents.AI.AgentResponseUpdate> ChatStreamingAsync(
            string input, ISessionHandle? handle, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var r in _responses)
            {
                await Task.Delay(1, ct);
                yield return new Microsoft.Agents.AI.AgentResponseUpdate(
                    new ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, r));
            }
        }

        public Task SaveSessionAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private StreamHandler CreateHandler(IStreamSource source)
    {
        var ui = new FakeStreamUI(_conv, _cache, _toolStatuses, _modifiedFiles, _spinnerStates, () => _refreshed = true);
        var dir = Path.Combine(Path.GetTempPath(), "ltai-tui-sessions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var sessionMgr = new LTAI.Core.Session.SessionManager(dir, new LTAI.Core.Session.JsonSessionSerializer());
        return new StreamHandler(ui, source, sessionMgr, _cache, _conv, _modifiedFiles);
    }

    private sealed class FakeStreamUI : IStreamUI
    {
        private readonly List<string> _conv;
        private readonly StringBuilder _cache;
        private readonly List<string> _toolStatuses;
        private readonly List<string> _modifiedFiles;
        private readonly List<string> _spinnerStates;
        private readonly Action _onRefresh;

        public FakeStreamUI(List<string> conv, StringBuilder cache, List<string> toolStatuses,
            List<string> modifiedFiles, List<string> spinnerStates, Action onRefresh)
        {
            _conv = conv; _cache = cache; _toolStatuses = toolStatuses;
            _modifiedFiles = modifiedFiles; _spinnerStates = spinnerStates; _onRefresh = onRefresh;
        }

        public void ShowSpinner(bool visible) => _spinnerStates.Add(visible ? "show" : "hide");
        public void SetStatusText(string text) => _toolStatuses.Add(text);
        public string GetConvEntry(int index) => _conv[index];
        public void SetConvEntry(int index, string value) { _conv[index] = value; }
        public void AddModifiedFile(string path) { if (!_modifiedFiles.Contains(path)) _modifiedFiles.Add(path); }
        public void RefreshStats() => _onRefresh();
    }

    [Fact]
    public async Task Stream_SingleResponse_AppendsToConv()
    {
        var h = CreateHandler(new FakeStreamSource("Hello!"));
        await h.StreamAsync("test", CancellationToken.None);
        Assert.Contains("Hello!", _conv[^1]);
    }

    [Fact]
    public async Task Stream_MultipleResponses_Concatenates()
    {
        var h = CreateHandler(new FakeStreamSource("Hello, ", "world!", " How are you?"));
        await h.StreamAsync("test", CancellationToken.None);
        Assert.Contains("Hello, world! How are you?", _conv[^1]);
    }

    [Fact]
    public async Task Stream_ShowsAndHidesSpinner()
    {
        var h = CreateHandler(new FakeStreamSource("done"));
        await h.StreamAsync("test", CancellationToken.None);
        Assert.Contains(_spinnerStates, s => s == "show");
        Assert.Contains(_spinnerStates, s => s == "hide");
    }

    [Fact]
    public async Task Stream_Cancellation_StopsEarly()
    {
        using var cts = new CancellationTokenSource();
        var h = CreateHandler(new FakeStreamSource("part1", "part2", "part3"));
        cts.CancelAfter(5);
        await h.StreamAsync("test", cts.Token);
        Assert.True(_conv.Count >= 1);
    }

    [Fact]
    public async Task Stream_EmptyText_DoesNotThrow()
    {
        var h = CreateHandler(new FakeStreamSource(""));
        await h.StreamAsync("test", CancellationToken.None);
        Assert.NotEmpty(_conv);
    }

    [Fact]
    public async Task Stream_SavesStatusOnComplete()
    {
        var h = CreateHandler(new FakeStreamSource("ok"));
        await h.StreamAsync("test", CancellationToken.None);
        Assert.Contains(_toolStatuses, s => s.Contains("就绪") || s.Contains("ready"));
    }
}
