using System.Text.RegularExpressions;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Options;

namespace LTAI.Agent.Tools;

/// <summary>
/// Determines whether a tool has been granted trusted (no-confirm) status
/// based on <see cref="ToolTrustConfig"/> configuration.
/// Supports glob-style patterns: "SafeShellTool.*", "*.RunCommand", "SafeShellTool.RunCommand"
/// </summary>
public sealed class ToolTrustService
{
    private readonly ToolTrustConfig _config;
    private readonly Regex[] _patterns;

    public ToolTrustService(IOptions<LTAIOptions> options)
    {
        _config = options.Value.ToolTrust;
        _patterns = _config.TrustedToolNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => GlobToRegex(n.Trim()))
            .ToArray();
    }

    /// <summary>
    /// Returns true if the given <paramref name="toolName"/> is trusted.
    /// Tool name format: "SafeShellTool.RunCommand" (ClassName.MethodName)
    /// </summary>
    public bool IsTrusted(string toolName)
    {
        if (_config.TrustAll) return true;
        if (string.IsNullOrWhiteSpace(toolName)) return false;
        foreach (var p in _patterns)
        {
            if (p.IsMatch(toolName)) return true;
        }
        return false;
    }

    /// <summary>
    /// Shorthand: checks if !IsTrusted(name).
    /// </summary>
    public bool RequiresConfirm(string toolName) => !IsTrusted(toolName);

    private static Regex GlobToRegex(string pattern)
    {
        var escaped = string.Concat(
            pattern.Select(c => c switch
            {
                '*' => ".*",
                '?' => ".",
                '.' => "\\.",
                _ => c.ToString()
            }));
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
