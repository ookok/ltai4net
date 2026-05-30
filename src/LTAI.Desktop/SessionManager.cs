using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.AI;

namespace LTAI.Desktop;

/// <summary>
/// 会话管理：创建/切换/删除对话，持久化到 .livingtree/sessions/
/// </summary>
public sealed class SessionManager
{
    private readonly string _sessionsDir;
    private readonly List<ChatMessage> _messages = new();
    private string _currentSession = "";
    private int _sessionCounter;

    public SessionManager()
    {
        _sessionsDir = Path.Combine(Directory.GetCurrentDirectory(), ".livingtree", "sessions");
        Directory.CreateDirectory(_sessionsDir);
        _sessionCounter = Directory.GetFiles(_sessionsDir, "*.json").Length;
    }

    public string CurrentSession => _currentSession;
    public List<ChatMessage> Messages => _messages;
    public int MessageCount => _messages.Count;

    public void AddMessage(ChatRole role, string content)
    {
        _messages.Add(new ChatMessage(role, content));
    }

    public string[] ListSessions()
    {
        return Directory.GetFiles(_sessionsDir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderByDescending(f => f)
            .ToArray();
    }

    public bool LoadSession(string name)
    {
        var path = Path.Combine(_sessionsDir, $"{name}.json");
        if (!File.Exists(path)) return false;
        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<List<ChatMessageJson>>(json);
            if (data == null) return false;
            _messages.Clear();
            foreach (var m in data)
                _messages.Add(new ChatMessage(m.Role == "user" ? ChatRole.User : ChatRole.Assistant, m.Content));
            _currentSession = name;
            return true;
        }
        catch { return false; }
    }

    public void SaveSession(string name)
    {
        var path = Path.Combine(_sessionsDir, $"{name}.json");
        var data = _messages.Select(m => new ChatMessageJson
        {
            Role = m.Role == ChatRole.User ? "user" : "assistant",
            Content = m.Text ?? ""
        }).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(data));
        _currentSession = name;
    }

    public string NewSession()
    {
        _sessionCounter++;
        var name = $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        _messages.Clear();
        _currentSession = name;
        return name;
    }

    public void DeleteSession(string name)
    {
        var path = Path.Combine(_sessionsDir, $"{name}.json");
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed record ChatMessageJson
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }
}
