using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace LTAI.Core.Session;

public sealed class SessionManager
{
    private readonly string _sessionsDir;
    private ISessionHandle? _currentHandle;
    private readonly int _maxSessions;
    private readonly int _keyRotationMonths;
    private static byte[]? _encryptionKey;
    private static DateTime? _keyCreatedAt;
    private static readonly object _keyLock = new();

    public ISessionHandle? CurrentHandle => _currentHandle;

    public string? CurrentSession => _currentHandle?.Name;

    public IReadOnlyList<ChatMessage> Messages => _currentHandle?.Messages ?? [];

    public int MessageCount => _currentHandle?.Messages.Count ?? 0;

    public SessionManager() : this(Options.Create(new LTAIOptions())) { }

    public SessionManager(IOptions<LTAIOptions> options)
    {
        var cfg = options.Value.Session;
        _sessionsDir = Path.IsPathRooted(cfg.Path)
            ? cfg.Path
            : Path.Combine(Directory.GetCurrentDirectory(), cfg.Path);
        _maxSessions = Math.Max(10, cfg.MaxSessions);
        _keyRotationMonths = Math.Max(0, cfg.KeyRotationMonths);
        EnsureKey();
        Directory.CreateDirectory(_sessionsDir);
    }

    public SessionManager(string sessionsDir, int maxSessions = 500, int keyRotationMonths = 6)
    {
        _sessionsDir = sessionsDir;
        _maxSessions = Math.Max(10, maxSessions);
        _keyRotationMonths = Math.Max(0, keyRotationMonths);
        EnsureKey();
        Directory.CreateDirectory(_sessionsDir);
    }

    public ISessionHandle NewSession()
    {
        var name = $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..4]}";
        _currentHandle = new JsonSessionHandle(name, null);
        return _currentHandle;
    }

    public SessionInfo[] ListSessions()
    {
        return Directory.GetFiles(_sessionsDir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderByDescending(f => f)
            .Select(f =>
            {
                var meta = ReadMetadata(f);
                return new SessionInfo(f, FormatSessionName(f), meta.ParentId);
            })
            .ToArray();
    }

    public SessionInfo[] ListChildSessions(string parentId)
    {
        return ListSessions().Where(s => s.ParentId == parentId).ToArray();
    }

    public ISessionHandle? LoadSession(string name)
    {
        var path = Path.Combine(_sessionsDir, $"{name}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path);
            // F12: Do NOT silently fall back to treating ciphertext as plaintext.
            // If decryption fails, the file is either corrupted or encrypted with a
            // different key — loading it as plaintext is a security downgrade.
            string json;
            try { json = Decrypt(text); }
            catch { return null; }

            var doc = JsonDocument.Parse(json);
            _currentHandle = new JsonSessionHandle(name, doc.RootElement.Clone());
            return _currentHandle;
        }
        catch { return null; }
    }

    public async Task<ISessionHandle?> LoadSessionAsync(string name)
    {
        var path = Path.Combine(_sessionsDir, $"{name}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            string json;
            try { json = Decrypt(text); }
            catch { return null; }

            var doc = JsonDocument.Parse(json);
            _currentHandle = new JsonSessionHandle(name, doc.RootElement.Clone());
            return _currentHandle;
        }
        catch { return null; }
    }

    public string CreateChildSession(string parentId, string label)
    {
        var counter = Directory.GetFiles(_sessionsDir, $"{parentId}*.json").Length + 1;
        var name = $"sub-{parentId}-{counter}";
        _currentHandle = new JsonSessionHandle(name, null);
        SaveMetadata(name, new SessionMeta { ParentId = parentId, Label = label });
        return name;
    }

    public void SaveSession()
    {
        if (_currentHandle != null)
            SaveSession(_currentHandle);
    }

    public void SaveSession(ISessionHandle handle)
    {
        var path = Path.Combine(_sessionsDir, $"{handle.Name}.json");
        var json = handle.SerializeToJson();
        File.WriteAllText(path, Encrypt(json));
        _currentHandle = handle;
        PruneOldSessions();
    }

    public async Task SaveSessionAsync()
    {
        if (_currentHandle != null)
            await SaveSessionAsync(_currentHandle).ConfigureAwait(false);
    }

    public async Task SaveSessionAsync(ISessionHandle handle)
    {
        var path = Path.Combine(_sessionsDir, $"{handle.Name}.json");
        var json = handle.SerializeToJson();
        await File.WriteAllTextAsync(path, Encrypt(json)).ConfigureAwait(false);
        _currentHandle = handle;
        PruneOldSessions();
    }

    public void SaveSession(string name)
    {
        if (_currentHandle == null) return;
        if (_currentHandle.Name == name)
        {
            SaveSession(_currentHandle);
            return;
        }
        // Save current state under a different name — wrap as new handle
        var copyHandle = new JsonSessionHandle(name, JsonDocument.Parse(_currentHandle.SerializeToJson()).RootElement);
        SaveSession(copyHandle);
    }

    public void DeleteSession(string name)
    {
        var path = Path.Combine(_sessionsDir, $"{name}.json");
        if (File.Exists(path)) File.Delete(path);
        var metaPath = Path.Combine(_sessionsDir, $"{name}.meta.json");
        if (File.Exists(metaPath)) File.Delete(metaPath);
    }

    public void SaveMetadata(string name, object meta)
    {
        var path = Path.Combine(_sessionsDir, $"{name}.meta.json");
        File.WriteAllText(path, JsonSerializer.Serialize(meta));
    }

    public static string FormatSessionName(string raw)
    {
        if (raw.StartsWith("session-") && raw.Length >= 22)
        {
            var d = raw.AsSpan(8);
            return $"{d[..4]}-{d[4..6]}-{d[6..8]} {d[9..11]}:{d[11..13]}";
        }
        return raw;
    }

    private sealed record SessionMeta
    {
        public string? ParentId { get; init; }
        public string? Label { get; init; }
        public long ElapsedMs { get; init; }
    }

    private SessionMeta ReadMetadata(string name)
    {
        var path = Path.Combine(_sessionsDir, $"{name}.meta.json");
        try { return JsonSerializer.Deserialize<SessionMeta>(File.ReadAllText(path)) ?? new SessionMeta(); }
        catch { return new SessionMeta(); }
    }

    private void PruneOldSessions()
    {
        var allSessions = ListSessions();
        if (allSessions.Length > _maxSessions)
        {
            var toDelete = allSessions.OrderBy(s => s.Name).Take(allSessions.Length - _maxSessions).ToArray();
            foreach (var s in toDelete)
                DeleteSession(s.Name);
        }
    }

    private void EnsureKey()
    {
        if (_encryptionKey != null) return;
        lock (_keyLock)
        {
            if (_encryptionKey != null) return;
            var keyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LTAI");
            var keyFile = Path.Combine(keyDir, "encryption.key");
            var metaFile = Path.Combine(keyDir, "encryption.meta");

            if (File.Exists(keyFile))
            {
                _encryptionKey = File.ReadAllBytes(keyFile);
                _keyCreatedAt = File.Exists(metaFile)
                    ? DateTime.Parse(File.ReadAllText(metaFile))
                    : DateTime.UtcNow;

                // 检查是否需要轮换
                if (_keyRotationMonths > 0 &&
                    (DateTime.UtcNow - _keyCreatedAt.Value).TotalDays >= _keyRotationMonths * 30)
                {
                    RotateKey(keyDir, keyFile, metaFile);
                }
            }
            else
            {
                _encryptionKey = RandomNumberGenerator.GetBytes(32);
                _keyCreatedAt = DateTime.UtcNow;
                Directory.CreateDirectory(keyDir);
                File.WriteAllBytes(keyFile, _encryptionKey);
                File.WriteAllText(metaFile, _keyCreatedAt.Value.ToString("O"));
            }
        }
    }

    private void RotateKey(string keyDir, string keyFile, string metaFile)
    {
        var oldKey = _encryptionKey;
        var newKey = RandomNumberGenerator.GetBytes(32);
        var failedFiles = new List<string>();

        // 用新密钥重新加密所有现有会话。
        // F12: 任何文件失败 → 中止旋转，保留旧密钥。
        var sessionFiles = Directory.GetFiles(_sessionsDir, "*.json");
        foreach (var sf in sessionFiles)
        {
            try
            {
                var ciphertext = File.ReadAllText(sf);
                var plaintext = Decrypt(ciphertext, oldKey!);
                _encryptionKey = newKey;
                File.WriteAllText(sf, Encrypt(plaintext));
                _encryptionKey = oldKey; // restore for next iteration
            }
            catch (Exception ex)
            {
                failedFiles.Add($"{sf}: {ex.Message}");
            }
        }

        if (failedFiles.Count > 0)
        {
            _encryptionKey = oldKey;
            var errors = string.Join("; ", failedFiles);
            throw new InvalidOperationException(
                $"Key rotation ABORTED: {failedFiles.Count} file(s) failed to re-encrypt: {errors}");
        }

        _encryptionKey = newKey;
        _keyCreatedAt = DateTime.UtcNow;
        File.WriteAllBytes(keyFile, newKey);
        File.WriteAllText(metaFile, _keyCreatedAt.Value.ToString("O"));
    }

    private static string Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = _encryptionKey ?? throw new InvalidOperationException("Encryption key not initialized");
        aes.GenerateIV();
        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        var result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);
        return Convert.ToBase64String(result);
    }

    private static string Decrypt(string ciphertext, byte[]? keyOverride = null)
    {
        var fullBytes = Convert.FromBase64String(ciphertext);
        using var aes = Aes.Create();
        aes.Key = keyOverride ?? _encryptionKey ?? throw new InvalidOperationException("Encryption key not initialized");
        var iv = new byte[aes.IV.Length];
        Buffer.BlockCopy(fullBytes, 0, iv, 0, iv.Length);
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        var cipherBytes = new byte[fullBytes.Length - iv.Length];
        Buffer.BlockCopy(fullBytes, iv.Length, cipherBytes, 0, cipherBytes.Length);
        var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
