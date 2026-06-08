using System.Collections.Concurrent;
using LTAI.Core.I18n;

namespace LTAI.Agent.Prompts;

public static class PromptLoader
{
    private static readonly string _promptsDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "agents");

    private static readonly ConcurrentDictionary<string, string> _cache = new();
    private static readonly FileSystemWatcher? _watcher;

    static PromptLoader()
    {
        try
        {
            if (Directory.Exists(_promptsDir))
            {
                _watcher = new FileSystemWatcher(_promptsDir, "*.prompt.md")
                {
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.LastWrite
                };
                _watcher.Changed += (_, _) => _cache.Clear();
            }
        }
        catch { }
    }

    /// <summary>Load prompt by name. Auto-resolves language suffix (zh/en).</summary>
    public static string Load(string name)
    {
        // First try with language suffix
        var lang = LTAI.Core.I18n.Locale.IsChinese ? "zh" : "en";
        var langKey = $"{name}-{lang}";
        if (_cache.TryGetValue(langKey, out var cached))
            return cached;

        var langPath = Path.Combine(_promptsDir, langKey + ".prompt.md");
        if (File.Exists(langPath))
        {
            var content = File.ReadAllText(langPath);
            _cache[langKey] = content;
            return content;
        }

        // Fallback: try without language suffix
        if (_cache.TryGetValue(name, out var fallback))
            return fallback;

        var path = Path.Combine(_promptsDir, name + ".prompt.md");
        if (!File.Exists(path))
            return "";

        var fallbackContent = File.ReadAllText(path);
        _cache[name] = fallbackContent;
        return fallbackContent;
    }

    /// <summary>Load prompt with placeholder replacements.</summary>
    public static string LoadWith(string name, params (string key, string value)[] replacements)
    {
        var template = Load(name);
        if (string.IsNullOrEmpty(template)) return "";

        foreach (var (key, value) in replacements)
            template = template.Replace("{{{" + key + "}}}", value);

        return template;
    }

    /// <summary>Bilingual load: zh = Chinese, en = English, defaults to zh if file missing.</summary>
    public static string LoadLang(string name)
    {
        var zh = Load($"{name}-zh");
        if (!string.IsNullOrEmpty(zh) && LTAI.Core.I18n.Locale.IsChinese)
            return zh;

        var en = Load($"{name}-en");
        if (!string.IsNullOrEmpty(en))
            return en;

        return zh; // fallback (may be empty)
    }

    public static void ClearCache() => _cache.Clear();
}
