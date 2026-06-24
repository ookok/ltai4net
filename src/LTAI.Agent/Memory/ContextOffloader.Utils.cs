// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ContextOffloader — Utility helpers: file detection, sanitization
// ═══════════════════════════════════════════════════════════════

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LTAI.Agent.Memory;

partial class ContextOffloader
{
    private static bool IsFileTool(string toolName)
    {
        var lower = toolName.ToLowerInvariant();
        return lower is "writefile" or "editfile" or "applypatch" or "filewritetool";
    }

    private static bool TryExtractFilePath(string arguments, out string filePath)
    {
        filePath = "";
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            if (doc.RootElement.TryGetProperty("path", out var p))
            {
                filePath = p.GetString() ?? "";
                return !string.IsNullOrEmpty(filePath);
            }
        }
        catch { }
        return false;
    }

    private static string SanitizeLabel(string label)
    {
        var sb = new StringBuilder(label.Length);
        foreach (var c in label)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                sb.Append(c);
            else
                sb.Append('-');
        }
        return sb.ToString().Trim('-').ToLowerInvariant();
    }

    private static string HexHash(string input, int length)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hex = Convert.ToHexStringLower(hash);
        return hex[..Math.Min(length, hex.Length)];
    }
}
