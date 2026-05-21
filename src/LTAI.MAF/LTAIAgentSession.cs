using LTAI.MAF.Hosting;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.MAF;

internal sealed class LTAIAgentSession : AgentSession
{
    private string? _sessionId;
    private static ILogger? _logger;

    public static void SetLogger(ILogger logger) => _logger = logger;

    public string SessionId
    {
        get
        {
            _sessionId ??= Guid.NewGuid().ToString("N")[..16];
            return _sessionId;
        }
    }

    public List<ChatMessage> History { get; } = new();

    public string? LastIntent { get; set; }
    public string? LastModel { get; set; }
    public int TurnCount { get; set; }

    public void AddTurn(string userQuery, string assistantResponse)
    {
        History.Add(new ChatMessage(ChatRole.User, userQuery));
        History.Add(new ChatMessage(ChatRole.Assistant, assistantResponse));
        TurnCount++;

        while (History.Count > 200)
        {
            History.RemoveAt(0);
            History.RemoveAt(0);
        }

        _ = PersistTurnAsync();
    }

    private async Task PersistTurnAsync()
    {
        try
        {
            await ChatHistoryManager.Instance.SaveAsync(new ChatSession
            {
                SessionId = this.SessionId,
                AgentName = "LTAI",
                Messages = History.Select(m => new Dictionary<string, string>
                {
                    ["role"] = m.Role == ChatRole.User ? "user" : "assistant",
                    ["content"] = m.Text ?? ""
                }).ToList(),
                IsComplete = false
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist session turn for {SessionId}", SessionId);
        }
    }

    public string? GetCompressedHistory(int maxTurns = 20)
    {
        var recent = History.TakeLast(maxTurns * 2).ToList();
        if (recent.Count == 0) return null;

        var parts = new List<string>();
        for (int i = 0; i < recent.Count; i += 2)
        {
            var q = recent[i].Text ?? "";
            var a = i + 1 < recent.Count ? recent[i + 1].Text ?? "" : "";
            parts.Add($"Q: {q[..Math.Min(q.Length, 200)]}\nA: {a[..Math.Min(a.Length, 200)]}");
        }
        return string.Join("\n", parts);
    }
}
