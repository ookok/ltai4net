using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.AI;

namespace LTAI.Desktop;

/// <summary>
/// 会话管理：创建/切换/删除对话，持久化到 .livingtree/sessions/
/// </summary>
public sealed record SessionInfo(string Name, string DisplayName);

public sealed class SessionManager
{
    private readonly string _sessionsDir;
    private readonly List<ChatMessage> _messages = new();
    private const int MaxSessions = 500;
    private static readonly byte[] EncryptionKey = ComputeEncryptionKey();

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

    public SessionInfo[] ListSessions()
    {
        return Directory.GetFiles(_sessionsDir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderByDescending(f => f)
            .Select(f => new SessionInfo(f, FormatSessionName(f)))
            .ToArray();
    }

    internal static string FormatSessionName(string raw)
    {
        if (raw.StartsWith("session-") && raw.Length >= 22)
        {
            var d = raw.AsSpan(8);
            return $"{d[..4]}-{d[4..6]}-{d[6..8]} {d[9..11]}:{d[11..13]}";
        }
        return raw;
    }

    public void SaveSession()
    {
        if (!string.IsNullOrEmpty(_currentSession))
            SaveSession(_currentSession);
    }

    public void SaveSession(string name)
    {
        var path = Path.Combine(_sessionsDir, $"{name}.json");
        var data = _messages.Select(m => new ChatMessageJson
        {
            Role = m.Role == ChatRole.User ? "user" : "assistant",
            Content = m.Text ?? ""
        }).ToList();
        var json = JsonSerializer.Serialize(data);
        File.WriteAllText(path, Encrypt(json));
        _currentSession = name;
        PruneOldSessions();
    }

    public bool LoadSession(string name)
    {
        var path = Path.Combine(_sessionsDir, $"{name}.json");
        if (!File.Exists(path)) return false;
        try
        {
            var text = File.ReadAllText(path);
            List<ChatMessageJson>? data;
            try { data = JsonSerializer.Deserialize<List<ChatMessageJson>>(text); }
            catch { data = JsonSerializer.Deserialize<List<ChatMessageJson>>(Decrypt(text)); }
            if (data == null) return false;
            _messages.Clear();
            foreach (var m in data)
                _messages.Add(new ChatMessage(m.Role == "user" ? ChatRole.User : ChatRole.Assistant, m.Content));
            _currentSession = name;
            return true;
        }
        catch { return false; }
    }

    public async Task<bool> LoadSessionAsync(string name)
    {
        var path = Path.Combine(_sessionsDir, $"{name}.json");
        if (!File.Exists(path)) return false;
        try
        {
            var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            List<ChatMessageJson>? data;
            try { data = JsonSerializer.Deserialize<List<ChatMessageJson>>(text); }
            catch { data = JsonSerializer.Deserialize<List<ChatMessageJson>>(Decrypt(text)); }
            if (data == null) return false;
            _messages.Clear();
            foreach (var m in data)
                _messages.Add(new ChatMessage(m.Role == "user" ? ChatRole.User : ChatRole.Assistant, m.Content));
            _currentSession = name;
            return true;
        }
        catch { return false; }
    }

    public async Task SaveSessionAsync()
    {
        if (!string.IsNullOrEmpty(_currentSession))
            await SaveSessionAsync(_currentSession).ConfigureAwait(false);
    }

    public async Task SaveSessionAsync(string name)
    {
        var path = Path.Combine(_sessionsDir, $"{name}.json");
        var data = _messages.Select(m => new ChatMessageJson
        {
            Role = m.Role == ChatRole.User ? "user" : "assistant",
            Content = m.Text ?? ""
        }).ToList();
        var json = JsonSerializer.Serialize(data);
        await File.WriteAllTextAsync(path, Encrypt(json)).ConfigureAwait(false);
        _currentSession = name;
        PruneOldSessions();
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

    private void PruneOldSessions()
    {
        var allSessions = ListSessions();
        if (allSessions.Length > MaxSessions)
        {
            var toDelete = allSessions.OrderBy(s => s.Name).Take(allSessions.Length - MaxSessions).ToArray();
            foreach (var s in toDelete)
                DeleteSession(s.Name);
        }
    }

    private static byte[] ComputeEncryptionKey()
    {
        using var sha256 = SHA256.Create();
        var material = $"{Environment.MachineName}-LTAI-v3";
        return sha256.ComputeHash(Encoding.UTF8.GetBytes(material));
    }

    private static string Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = EncryptionKey;
        aes.GenerateIV();
        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        var result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);
        return Convert.ToBase64String(result);
    }

    private static string Decrypt(string ciphertext)
    {
        var fullBytes = Convert.FromBase64String(ciphertext);
        using var aes = Aes.Create();
        aes.Key = EncryptionKey;
        var iv = new byte[aes.IV.Length];
        Buffer.BlockCopy(fullBytes, 0, iv, 0, iv.Length);
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        var cipherBytes = new byte[fullBytes.Length - iv.Length];
        Buffer.BlockCopy(fullBytes, iv.Length, cipherBytes, 0, cipherBytes.Length);
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private sealed record ChatMessageJson
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }
}
