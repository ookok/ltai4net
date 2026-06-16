using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Core.Session;

public sealed class SessionManager
{
    private readonly string _sessionsDir;
    private volatile ISessionHandle? _currentHandle;
    private readonly int _maxSessions;
    private readonly int _keyRotationMonths;
    private readonly ISessionSerializer _serializer;
    private static byte[]? _encryptionKey;
    private static DateTime? _keyCreatedAt;
    private static readonly object _keyLock = new();
    private int _saveCount;
    private const int PruneInterval = 5;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Callback invoked when a session is deleted. Subscribers should invalidate any session-scoped caches.</summary>
    public Action<string>? OnSessionDeleted;

    public ISessionHandle? CurrentHandle => _currentHandle;

    public string? CurrentSession => _currentHandle?.Name;

    public IReadOnlyList<ChatMessage> Messages => _currentHandle?.Messages ?? [];

    public int MessageCount => _currentHandle?.Messages.Count ?? 0;

    public string FileExtension => _serializer.FileExtension;

    public SessionManager() : this(Options.Create(new LTAIOptions())) { }

    public SessionManager(IOptions<LTAIOptions> options) : this(options, new JsonSessionSerializer()) { }

    public SessionManager(IOptions<LTAIOptions> options, ISessionSerializer serializer)
    {
        var cfg = options.Value.Session;
        _sessionsDir = Path.IsPathRooted(cfg.Path)
            ? cfg.Path
            : Path.Combine(Directory.GetCurrentDirectory(), cfg.Path);
        _maxSessions = Math.Max(10, cfg.MaxSessions);
        _keyRotationMonths = Math.Max(0, cfg.KeyRotationMonths);
        _serializer = serializer;
        EnsureKey();
        Directory.CreateDirectory(_sessionsDir);
        CleanOrphanedTempFiles();
    }

    public SessionManager(ISessionSerializer serializer)
        : this(new LTAIOptions().Session.Path, serializer) { }

    public SessionManager(string sessionsDir, int maxSessions = 500, int keyRotationMonths = 6)
        : this(sessionsDir, new JsonSessionSerializer(), maxSessions, keyRotationMonths) { }

    public SessionManager(string sessionsDir, ISessionSerializer serializer, int maxSessions = 500, int keyRotationMonths = 6)
    {
        _sessionsDir = sessionsDir;
        _maxSessions = Math.Max(10, maxSessions);
        _keyRotationMonths = Math.Max(0, keyRotationMonths);
        _serializer = serializer;
        EnsureKey();
        Directory.CreateDirectory(_sessionsDir);
        CleanOrphanedTempFiles();
    }

    public ISessionHandle NewSession()
    {
        var name = $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..4]}";
        _currentHandle = new JsonSessionHandle(name, null);
        return _currentHandle;
    }

    public SessionInfo[] ListSessions()
    {
        var ext = _serializer.FileExtension;
        var metaExt = ".meta" + ext;
        return Directory.GetFiles(_sessionsDir, SessionSearchPattern)
            .Select(Path.GetFileName)
            .Where(f => f != null && f.EndsWith(ext, StringComparison.Ordinal))
            .Where(f => !f!.EndsWith(metaExt, StringComparison.Ordinal))
            .Select(f => f![..^ext.Length])
            .Distinct()
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
        var path = SessionPath(name);
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path);
            string data;
            try { data = Decrypt(text); }
            catch (CryptographicException)
            {
                Console.Error.WriteLine($"[LTAI] Session '{name}' integrity check failed (tampered or corrupted).");
                return null;
            }
            catch (FormatException)
            {
                Console.Error.WriteLine($"[LTAI] Session '{name}' is not valid Base64 (truncated or corrupted).");
                return null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LTAI] Session '{name}' decryption failed: {ex.Message}");
                return null;
            }

            var el = _serializer.Deserialize(data);
            _currentHandle = new JsonSessionHandle(name, el);
            return _currentHandle;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LTAI] Session '{name}' deserialization failed: {ex.Message}");
            return null;
        }
    }

    public async Task<ISessionHandle?> LoadSessionAsync(string name)
    {
        var path = SessionPath(name);
        if (!File.Exists(path)) return null;
        try
        {
            var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            string data;
            try { data = Decrypt(text); }
            catch (CryptographicException)
            {
                Console.Error.WriteLine($"[LTAI] Session '{name}' integrity check failed (tampered or corrupted).");
                return null;
            }
            catch (FormatException)
            {
                Console.Error.WriteLine($"[LTAI] Session '{name}' is not valid Base64 (truncated or corrupted).");
                return null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LTAI] Session '{name}' decryption failed: {ex.Message}");
                return null;
            }

            var el = _serializer.Deserialize(data);
            _currentHandle = new JsonSessionHandle(name, el);
            return _currentHandle;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LTAI] Session '{name}' deserialization failed: {ex.Message}");
            return null;
        }
    }

    public string CreateChildSession(string parentId, string label)
    {
        var counter = Directory.GetFiles(_sessionsDir, $"{parentId}*{_serializer.FileExtension}").Length + 1;
        var name = $"sub-{parentId}-{counter}";
        _currentHandle = new JsonSessionHandle(name, null);
        SaveMetadata(name, new SessionMeta { ParentId = parentId, Label = label });
        return name;
    }

    private readonly object _currentHandleLock = new();

    public void SaveSession()
    {
        ISessionHandle? handle;
        lock (_currentHandleLock) { handle = _currentHandle; }
        if (handle != null)
            SaveSession(handle);
    }

    public void SaveSession(ISessionHandle handle)
    {
        lock (_currentHandleLock) { _currentHandle = handle; }
        var path = SessionPath(handle.Name);
        var json = handle.SerializeToJson();
        var serialized = _serializer.Serialize(JsonDocument.Parse(json).RootElement);
        AtomicWrite(path, Encrypt(serialized));
        if (++_saveCount % PruneInterval == 0)
            PruneOldSessions();
    }

    public async Task SaveSessionAsync()
    {
        ISessionHandle? handle;
        lock (_currentHandleLock) { handle = _currentHandle; }
        if (handle != null)
            await SaveSessionAsync(handle).ConfigureAwait(false);
    }

    public async Task SaveSessionAsync(ISessionHandle handle)
    {
        lock (_currentHandleLock) { _currentHandle = handle; }
        var path = SessionPath(handle.Name);
        var json = handle.SerializeToJson();
        var serialized = _serializer.Serialize(JsonDocument.Parse(json).RootElement);
        await AtomicWriteAsync(path, Encrypt(serialized)).ConfigureAwait(false);
        if (++_saveCount % PruneInterval == 0)
            PruneOldSessions();
    }

    public void SaveSession(string name)
    {
        ISessionHandle? handle;
        lock (_currentHandleLock) { handle = _currentHandle; }
        if (handle == null) return;
        if (handle.Name == name)
        {
            SaveSession(handle);
            return;
        }
        var copyHandle = new JsonSessionHandle(name, JsonDocument.Parse(handle.SerializeToJson()).RootElement);
        SaveSession(copyHandle);
    }

    public void DeleteSession(string name)
    {
        var path = SessionPath(name);
        if (File.Exists(path)) File.Delete(path);
        var metaPath = MetaPath(name);
        if (File.Exists(metaPath)) File.Delete(metaPath);
        OnSessionDeleted?.Invoke(name);
    }

    public void SaveMetadata(string name, object meta)
    {
        var path = Path.Combine(_sessionsDir, $"{name}.meta.json");
        File.WriteAllText(path, JsonSerializer.Serialize(meta, _jsonOpts));
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

    private string SessionPath(string name) =>
        Path.Combine(_sessionsDir, $"{name}{_serializer.FileExtension}");

    private string MetaPath(string name) =>
        Path.Combine(_sessionsDir, $"{name}.meta.json");

    private string SessionSearchPattern => $"*{_serializer.FileExtension}";

    private SessionMeta ReadMetadata(string name)
    {
        var path = Path.Combine(_sessionsDir, $"{name}.meta.json");
        try { return JsonSerializer.Deserialize<SessionMeta>(File.ReadAllText(path), _jsonOpts) ?? new SessionMeta(); }
        catch { return new SessionMeta(); }
    }

    private void PruneOldSessions()
    {
        var allSessions = ListSessions();
        if (allSessions.Length > _maxSessions)
        {
            var toDelete = allSessions
                .OrderBy(s =>
                {
                    try { return File.GetLastWriteTimeUtc(SessionPath(s.Name)); }
                    catch { return DateTime.MinValue; }
                })
                .Take(allSessions.Length - _maxSessions)
                .ToArray();
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

            // Priority 1: environment variable
            var envKey = Environment.GetEnvironmentVariable("LTAI_ENCRYPTION_KEY");
            if (!string.IsNullOrEmpty(envKey))
            {
                try
                {
                    var keyBytes = Convert.FromBase64String(envKey);
                    if (keyBytes.Length == 32)
                    {
                        _encryptionKey = keyBytes;
                        _keyCreatedAt = DateTime.UtcNow;
                        return;
                    }
                    Console.Error.WriteLine("[LTAI] LTAI_ENCRYPTION_KEY is not 32 bytes. Falling back to file-based key.");
                }
                catch
                {
                    Console.Error.WriteLine("[LTAI] LTAI_ENCRYPTION_KEY is not valid Base64. Falling back to file-based key.");
                }
            }

            var keyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LTAI");
            var keyFile = Path.Combine(keyDir, "encryption.key");
            var metaFile = Path.Combine(keyDir, "encryption.meta");

            if (File.Exists(keyFile))
            {
                _encryptionKey = File.ReadAllBytes(keyFile);
                _keyCreatedAt = File.Exists(metaFile)
                    ? DateTime.Parse(File.ReadAllText(metaFile), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
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
                // Log warning: key loss = permanent session data loss
                Console.Error.WriteLine($"[LTAI] Session encryption key generated at: {keyFile}");
                Console.Error.WriteLine("[LTAI] WARNING: Losing this file makes ALL sessions permanently unreadable. Back it up.");
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
        var sessionFiles = Directory.GetFiles(_sessionsDir, SessionSearchPattern);
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

    // Schema version: v1 adds magic header + HMAC for integrity verification.
    // v0 (legacy, no header) is still readable for migration.
    private const byte SessionSchemaVersion = 0x01;
    private static readonly byte[] MagicPrefix = [0x4C, 0x54]; // "LT"

    private static string Encrypt(string plaintext)
    {
        var key = _encryptionKey ?? throw new InvalidOperationException("Encryption key not initialized");
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Build payload: magic(2) || version(1) || IV || ciphertext
        var payloadLen = 2 + 1 + aes.IV.Length + cipherBytes.Length;
        var payload = new byte[payloadLen];
        Buffer.BlockCopy(MagicPrefix, 0, payload, 0, 2);
        payload[2] = SessionSchemaVersion;
        Buffer.BlockCopy(aes.IV, 0, payload, 3, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, payload, 3 + aes.IV.Length, cipherBytes.Length);

        // Append HMAC-SHA256 over the payload for integrity verification
        using var hmac = new HMACSHA256(key);
        var mac = hmac.ComputeHash(payload);

        var result = new byte[payload.Length + mac.Length];
        Buffer.BlockCopy(payload, 0, result, 0, payload.Length);
        Buffer.BlockCopy(mac, 0, result, payload.Length, mac.Length);
        return Convert.ToBase64String(result);
    }

    private static string Decrypt(string ciphertext, byte[]? keyOverride = null)
    {
        var fullBytes = Convert.FromBase64String(ciphertext);
        var key = keyOverride ?? _encryptionKey ?? throw new InvalidOperationException("Encryption key not initialized");

        // Detect format: v1 has magic prefix (0x4C, 0x54) followed by version byte.
        bool isV1 = fullBytes.Length >= 3 && fullBytes[0] == MagicPrefix[0] && fullBytes[1] == MagicPrefix[1];

        if (isV1)
        {
            var version = fullBytes[2];
            if (version != SessionSchemaVersion)
                throw new InvalidOperationException($"Unsupported session schema version: {version}");

            if (fullBytes.Length < 3 + 16 + 32) // min: magic(2) + ver(1) + IV(16) + HMAC-SHA256(32)
                throw new CryptographicException("Session data truncated (v1)");

            var payloadLen = fullBytes.Length - 32; // last 32 bytes = HMAC
            var payload = new byte[payloadLen];
            Buffer.BlockCopy(fullBytes, 0, payload, 0, payloadLen);

            // Verify HMAC
            using var hmac = new HMACSHA256(key);
            var expectedMac = hmac.ComputeHash(payload);
            var actualMac = new byte[32];
            Buffer.BlockCopy(fullBytes, payloadLen, actualMac, 0, 32);

            if (!CryptographicOperations.FixedTimeEquals(expectedMac, actualMac))
                throw new CryptographicException("Session data integrity check failed (HMAC mismatch)");

            // Extract IV (at offset 3) and ciphertext
            using var aes = Aes.Create();
            aes.Key = key;
            var ivLen = aes.IV.Length;
            var iv = new byte[ivLen];
            Buffer.BlockCopy(payload, 3, iv, 0, ivLen);
            aes.IV = iv;

            var cipherLen = payloadLen - 3 - ivLen;
            var cipherData = new byte[cipherLen];
            Buffer.BlockCopy(payload, 3 + ivLen, cipherData, 0, cipherLen);

            using var decryptor = aes.CreateDecryptor();
            var plainBytes = decryptor.TransformFinalBlock(cipherData, 0, cipherData.Length);
            return Encoding.UTF8.GetString(plainBytes);
        }
        else
        {
            // Legacy v0 format: IV || ciphertext (no magic, no HMAC)
            using var aes = Aes.Create();
            aes.Key = key;
            var ivLen = aes.IV.Length;
            if (fullBytes.Length < ivLen + 1)
                throw new CryptographicException("Session data truncated (v0)");

            var iv = new byte[ivLen];
            Buffer.BlockCopy(fullBytes, 0, iv, 0, ivLen);
            aes.IV = iv;

            var cipherLen = fullBytes.Length - ivLen;
            var cipherData = new byte[cipherLen];
            Buffer.BlockCopy(fullBytes, ivLen, cipherData, 0, cipherLen);

            using var decryptor = aes.CreateDecryptor();
            var plainBytes = decryptor.TransformFinalBlock(cipherData, 0, cipherData.Length);
            return Encoding.UTF8.GetString(plainBytes);
        }
    }

    /// <summary>Atomic file write: write to .tmp file → rename to target (NTFS atomic).</summary>
    private static void AtomicWrite(string path, string content)
    {
        var tmpPath = path + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllText(tmpPath, content);
            File.Move(tmpPath, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch
            {
                // non-critical, best-effort
            }
            throw;
        }
    }

    /// <summary>Atomic file write: write to .tmp file → rename to target (NTFS atomic).</summary>
    private static async Task AtomicWriteAsync(string path, string content)
    {
        var tmpPath = path + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await File.WriteAllTextAsync(tmpPath, content).ConfigureAwait(false);
            File.Move(tmpPath, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch
            {
                // non-critical, best-effort
            }
            throw;
        }
    }

    private void CleanOrphanedTempFiles()
    {
        try
        {
            var ext = _serializer.FileExtension;
            foreach (var tmp in Directory.GetFiles(_sessionsDir, $"*{ext}.tmp.*"))
            {
                try { File.Delete(tmp); } catch
                {
                    // non-critical, best-effort
                }
            }
        }
        catch
        {
            // non-critical, best-effort
        }
    }
}

public sealed class JsonSessionSerializer : ISessionSerializer
{
    public string FileExtension => ".json";

    public string Serialize(JsonElement state)
    {
        return state.GetRawText();
    }

    public JsonElement Deserialize(string data)
    {
        return JsonDocument.Parse(data).RootElement.Clone();
    }
}

/// <summary>Utility to check encryption key status.</summary>
public static class SessionKeyInfo
{
    public static string KeyPath
    {
        get
        {
            var keyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LTAI");
            return Path.Combine(keyDir, "encryption.key");
        }
    }

    public static bool KeyExists => File.Exists(KeyPath);

    public static string GetDiagnostics()
    {
        if (!KeyExists)
            return $"Key not found at: {KeyPath}\nSet LTAI_ENCRYPTION_KEY env var or sessions will auto-generate a key.";
        var created = File.GetCreationTimeUtc(KeyPath);
        var age = DateTime.UtcNow - created;
        return $"Key path: {KeyPath}\nAge: {age.TotalDays:F0} days\nWARNING: Lose this file = all sessions permanently unreadable.";
    }
}
