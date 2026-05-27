using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LTAI.Core.Governors;

namespace LTAI.Core.System;

public sealed record CapTokenInfo
{
    public bool Valid { get; init; }
    public string Subject { get; init; } = "";
    public KernelPermission Permissions { get; init; }
    public string TargetPath { get; init; } = "";
    public DateTime Expiry { get; init; }
    public string? Reason { get; init; }
}

public sealed class KernelCapToken
{
    private readonly byte[] _signingKey;
    private readonly string _workspaceRoot;
    private readonly ConcurrentDictionary<string, DateTime> _revoked = new();

    public KernelCapToken(string workspaceRoot, byte[]? signingKey = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _signingKey = signingKey ?? SHA256.HashData(
            Encoding.UTF8.GetBytes($"ltai-cap-token-{Environment.MachineName}-{_workspaceRoot}"));
    }

    public string Issue(string subject, KernelPermission permissions, string targetPath, TimeSpan ttl)
    {
        targetPath = ResolvePath(targetPath);
        var expiry = DateTime.UtcNow + ttl;

        var payload = JsonSerializer.Serialize(new
        {
            sub = subject,
            perm = (int)permissions,
            path = targetPath,
            exp = expiry.ToString("O")
        });

        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hmac = HMACSHA256.HashData(_signingKey, payloadBytes);
        var combined = new byte[payloadBytes.Length + hmac.Length];
        Buffer.BlockCopy(payloadBytes, 0, combined, 0, payloadBytes.Length);
        Buffer.BlockCopy(hmac, 0, combined, payloadBytes.Length, hmac.Length);

        return Convert.ToBase64String(combined).TrimEnd('=');
    }

    public CapTokenInfo Validate(string token)
    {
        if (_revoked.ContainsKey(token))
            return new CapTokenInfo { Reason = "token revoked" };

        try
        {
            var combined = Convert.FromBase64String(token.PadRight(
                (token.Length + 3) / 4 * 4, '='));

            if (combined.Length < 32 + 16) return new CapTokenInfo { Reason = "invalid token format" };

            var hmacLen = 32;
            var payloadLen = combined.Length - hmacLen;
            var payloadBytes = new byte[payloadLen];
            var hmacBytes = new byte[hmacLen];
            Buffer.BlockCopy(combined, 0, payloadBytes, 0, payloadLen);
            Buffer.BlockCopy(combined, payloadLen, hmacBytes, 0, hmacLen);

            var expectedHmac = HMACSHA256.HashData(_signingKey, payloadBytes);
            if (!hmacBytes.AsSpan().SequenceEqual(expectedHmac.AsSpan()))
                return new CapTokenInfo { Reason = "invalid signature" };

            var payload = JsonSerializer.Deserialize<JsonElement>(Encoding.UTF8.GetString(payloadBytes));
            var subject = payload.GetProperty("sub").GetString() ?? "";
            var perm = (KernelPermission)payload.GetProperty("perm").GetInt32();
            var path = payload.GetProperty("path").GetString() ?? "";
            var exp = DateTime.Parse(payload.GetProperty("exp").GetString()!);

            if (DateTime.UtcNow > exp)
                return new CapTokenInfo { Reason = "token expired", Subject = subject };

            if (!Directory.Exists(path) && !File.Exists(path) &&
                !Path.EndsInDirectorySeparator(path))
                return new CapTokenInfo { Reason = "target path not found", Subject = subject };

            if (_revoked.ContainsKey(token))
                return new CapTokenInfo { Reason = "token revoked during validation", Subject = subject };

            return new CapTokenInfo
            {
                Valid = true,
                Subject = subject,
                Permissions = perm,
                TargetPath = path,
                Expiry = exp
            };
        }
        catch (Exception ex)
        {
            return new CapTokenInfo { Reason = $"validation error: {ex.Message}" };
        }
    }

    public void Revoke(string token)
    {
        _revoked[token] = DateTime.UtcNow;
    }

    public string ResolvePath(string targetPath)
    {
        if (Path.IsPathRooted(targetPath))
        {
            var fullPath = Path.GetFullPath(targetPath);
            if (fullPath.StartsWith(Path.GetFullPath(_workspaceRoot),
                StringComparison.OrdinalIgnoreCase))
                return fullPath;
            throw new UnauthorizedAccessException(
                $"CapToken target path {targetPath} is outside workspace {_workspaceRoot}");
        }
        return Path.GetFullPath(Path.Combine(_workspaceRoot, targetPath));
    }
}
