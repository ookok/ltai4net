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
        var sb = new System.Text.StringBuilder(pattern.Length * 2);
        foreach (var c in pattern)
        {
            switch (c)
            {
                case '*': sb.Append(".*"); break;
                case '?': sb.Append('.'); break;
                case '.':
                case '(': case ')': case '[': case ']':
                case '{': case '}': case '^': case '$':
                case '+': case '|': case '\\':
                    sb.Append('\\'); sb.Append(c); break;
                default: sb.Append(c); break;
            }
        }
        return new Regex($"^{sb}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
