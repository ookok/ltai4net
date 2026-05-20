using LTAI.Core.Session;
using Xunit;

namespace LTAI.Tests;

public class SessionStoreTests
{
    [Fact]
    public void GetOrCreate_CreatesNewSession()
    {
        var store = new InMemorySessionStore();
        var session = store.GetOrCreate("s1");
        Assert.Equal("s1", session.SessionId);
        Assert.Empty(session.Turns);
    }

    [Fact]
    public void GetOrCreate_ReturnsExistingSession()
    {
        var store = new InMemorySessionStore();
        var s1 = store.GetOrCreate("s1");
        s1.AddTurn("q1", "r1");
        var s2 = store.GetOrCreate("s1");
        Assert.Single(s2.Turns);
        Assert.Equal("q1", s2.Turns[0].UserQuery);
    }

    [Fact]
    public void AddTurn_EvictOldest_WhenExceedsMaxTurns()
    {
        var session = new SessionState { SessionId = "s1", MaxTurns = 3 };
        for (int i = 0; i < 10; i++)
            session.AddTurn($"q{i}", $"r{i}");
        Assert.Equal(3, session.Turns.Count);
        Assert.Equal("q7", session.Turns[0].UserQuery);
    }

    [Fact]
    public void GetCompressedHistory_TieredCompaction()
    {
        var session = new SessionState { SessionId = "s1" };
        for (int i = 0; i < 10; i++)
            session.AddTurn(new string('q', 10) + i, new string('r', 10) + i);

        var history = session.GetCompressedHistory(recentFull: 2, summaryRange: 3);
        Assert.Contains("Q:", history);
        Assert.Contains("[summary]", history);
    }

    [Fact]
    public void Delete_RemovesSession()
    {
        var store = new InMemorySessionStore();
        store.GetOrCreate("s1");
        store.Delete("s1");
        Assert.Null(store.Get("s1"));
    }

    [Fact]
    public void ListActive_FiltersByAge()
    {
        var store = new InMemorySessionStore();
        store.GetOrCreate("s1");
        var s2 = store.GetOrCreate("s2");
        s2.LastActivity = DateTime.UtcNow - TimeSpan.FromHours(48);

        var active = store.ListActive(TimeSpan.FromHours(24));
        Assert.Single(active);
    }
}
