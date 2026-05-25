using System.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.AI;

namespace LTAI.AI.Providers;

public sealed class PrefixCacheStore
{
    private string _systemHash = "";
    private string _toolHash = "";
    private readonly StringBuilder _conversationLog = new();
    private readonly object _lock = new();
    private int _turnCount;

    public int TurnCount => _turnCount;

    public string SystemHash => _systemHash;
    public string ToolHash => _toolHash;

    public void SetSystemPrompt(string systemPrompt)
    {
        _systemHash = ComputeSha256(systemPrompt);
    }

    public void SetToolDefinitions(string toolsJson)
    {
        _toolHash = ComputeSha256(toolsJson);
    }

    public void AppendTurn(string userMessage, string assistantResponse)
    {
        lock (_lock)
        {
            _conversationLog.Append(userMessage);
            _conversationLog.Append('\n');
            _conversationLog.Append(assistantResponse);
            _conversationLog.Append('\n');
            _turnCount++;
        }
    }

    public string GetConversationLog()
    {
        lock (_lock) return _conversationLog.ToString();
    }

    public void ResetVolatileLog()
    {
        lock (_lock)
        {
            _conversationLog.Clear();
            _turnCount = 0;
        }
    }

    public List<ChatMessage> BuildMessages(
        ChatMessage systemMessage,
        List<AITool> tools,
        List<ChatMessage>? conversationHistory)
    {
        var messages = new List<ChatMessage> { systemMessage };

        if (tools.Count > 0)
        {
            var toolsDesc = string.Join("|", tools.Select(t => $"{t.Name}:{t.Description}"));
            SetToolDefinitions(toolsDesc);
        }

        if (conversationHistory != null)
        {
            foreach (var msg in conversationHistory)
                messages.Add(msg);
        }

        return messages;
    }

    public string GetCacheStats()
    {
        lock (_lock)
        {
            return $"turn_count={_turnCount} system_hash={_systemHash[..Math.Min(_systemHash.Length, 8)]} tool_hash={_toolHash[..Math.Min(_toolHash.Length, 8)]}";
        }
    }

    private static string ComputeSha256(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash);
    }
}
