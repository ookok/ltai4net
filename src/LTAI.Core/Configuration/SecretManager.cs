using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.Core.Configuration;

#pragma warning disable CA1416 // DPAPI is gated by OperatingSystem.IsWindows() at runtime

/// <summary>
/// Centralized API key manager. Keys stored ONLY in environment variables.
/// Config files (provider_endpoints.md, appsettings.json) store endpoint/model only — never keys.
/// On Windows, secrets are DPAPI-encrypted when persisted to User scope (Set with persistent=true).
/// On Linux/macOS, falls back to unencrypted User env var store (best-effort).
/// ⚠ Cache has 5-minute TTL — env var changes need Invalidate() to take effect.
/// <b>Consumers:</b> MultiProviderChatClient, EmbeddingClient (Get for LLM calls);
/// WebTools, IntegrationTools (Get for web/map APIs);
/// Cli/Program.cs, ConfigView (Set for key configuration);
/// Tests (CoreTests).
/// </summary>
public static class SecretManager
{
    /// <summary>Optional logger set by DI during startup for DPAPI failure diagnostics.</summary>
    public static ILogger? Logger { get; set; }

    private static readonly ConcurrentDictionary<string, (string? value, DateTime cached)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>Read secret: cache (with TTL check) → Process env → User env → Machine env.</summary>
    public static string? Get(string envVar)
    {
        if (_cache.TryGetValue(envVar, out var entry) && (DateTime.UtcNow - entry.cached) < CacheTtl)
            return entry.value;

        var val = Environment.GetEnvironmentVariable(envVar, EnvironmentVariableTarget.Process)
               ?? DecryptIfNeeded(Environment.GetEnvironmentVariable(envVar, EnvironmentVariableTarget.User))
               ?? Environment.GetEnvironmentVariable(envVar, EnvironmentVariableTarget.Machine);
        _cache[envVar] = (val, DateTime.UtcNow);
        return val;
    }

    /// <summary>Write secret to runtime cache + persist to User scope (encrypted on Windows).</summary>
    public static void Set(string envVar, string? value, bool persistent = false)
    {
        _cache[envVar] = (value, DateTime.UtcNow);
        Environment.SetEnvironmentVariable(envVar, value, EnvironmentVariableTarget.Process);
        if (persistent)
        {
            try
            {
                var encrypted = EncryptIfNeeded(value);
                Environment.SetEnvironmentVariable(envVar, encrypted, EnvironmentVariableTarget.User);
            }
            catch (Exception ex) { Logger?.LogWarning(ex, "SecretManager: 持久化密钥失败 (非致命)"); }
        }
    }

    /// <summary>Check if a secret is set and non-empty.</summary>
    public static bool Has(string envVar) => !string.IsNullOrEmpty(Get(envVar));

    /// <summary>Invalidate cache to force re-read from environment on next Get.</summary>
    public static void Invalidate(string envVar) => _cache.TryRemove(envVar, out _);

    private static readonly bool _isWindows = OperatingSystem.IsWindows();

    /// <summary>DPAPI-encrypt a secret for User-scope env var storage. On non-Windows, returns raw value.</summary>
    private static string? EncryptIfNeeded(string? value)
    {
        if (string.IsNullOrEmpty(value) || !_isWindows) return value;
        try
        {
            var plain = System.Text.Encoding.UTF8.GetBytes(value);
            var encrypted = System.Security.Cryptography.ProtectedData.Protect(plain, null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return "DPAPI:" + Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "SecretManager: DPAPI 加密失败，回退明文存储");
            return value;
        }
    }

    /// <summary>DPAPI-decrypt a User-scope env var. Strips "DPAPI:" prefix before decrypting.</summary>
    private static string? DecryptIfNeeded(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith("DPAPI:") || !_isWindows) return value;
        try
        {
            var encrypted = Convert.FromBase64String(value[6..]);
            var plain = System.Security.Cryptography.ProtectedData.Unprotect(encrypted, null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "SecretManager: DPAPI 解密失败，返回原始值");
            return value;
        }
    }
}

#pragma warning restore CA1416
