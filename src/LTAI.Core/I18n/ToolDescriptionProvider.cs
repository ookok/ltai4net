using System.Collections.Concurrent;
using System.Reflection;

namespace LTAI.Core.I18n;

/// <summary>
/// B4: Provides localized tool descriptions for AITool instances.
/// Maintains an in-memory cache of (toolName, lang) → description.
/// Falls back to the original [Description] attribute value (Chinese) when
/// no translation is registered.
///
/// <b>Registration:</b>
/// <code>
///   ToolDescriptionProvider.Register("SafeShellTool", "en", "Execute a shell command safely");
/// </code>
///
/// Design decision: translations are registered programmatically at startup
/// rather than loaded from .resx, keeping the system self-contained. A future
/// enhancement could load from JSON files.
/// </summary>
public static class ToolDescriptionProvider
{
    private static readonly ConcurrentDictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Register a translation for a tool name in a given language.</summary>
    public static void Register(string toolName, string lang, string description)
    {
        _overrides[$"{lang}::{toolName}"] = description;
    }

    /// <summary>Get localized description, or null if none registered.</summary>
    public static string? Get(string toolName, string lang)
    {
        return _overrides.TryGetValue($"{lang}::{toolName}", out var desc) ? desc : null;
    }

    /// <summary>
    /// Resolve the best available description: explicit lang override → zh-CN → null.
    /// Callers fall back to the original [Description] attribute.
    /// </summary>
    public static string? Resolve(string toolName)
    {
        var lang = Locale.CurrentLang;
        return Get(toolName, lang) ?? Get(toolName, "zh-CN");
    }
}
