using System.Collections.Concurrent;
using LTAI.Core.I18n;

namespace LTAI.Agent.Prompts;

public static class PromptLoader
{
    private static string? _promptsDir;

    private static string PromptsDir
    {
        get
        {
            if (_promptsDir != null) return _promptsDir;
            var dir = AppContext.BaseDirectory ?? Directory.GetCurrentDirectory();
            while (dir != null && !Directory.Exists(Path.Combine(dir, "agents")))
            {
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
            _promptsDir = Path.Combine(dir ?? "", "agents");
            return _promptsDir;
        }
    }

    private static readonly ConcurrentDictionary<string, string> _cache = new();
    private static readonly FileSystemWatcher? _watcher;

    static PromptLoader()
    {
        try
        {
            if (Directory.Exists(PromptsDir))
            {
                _watcher = new FileSystemWatcher(PromptsDir, "*.prompt.md")
                {
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.LastWrite
                };
                _watcher.Changed += (_, _) => _cache.Clear();
                AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { _watcher?.Dispose(); } catch
                {
                    // non-critical, best-effort
                } };
            }
        }
        catch
        {
            // non-critical, best-effort
        }
    }

    /// <summary>Load prompt by name. Auto-resolves language suffix (zh/en).</summary>
    public static string Load(string name)
    {
        // First try with language suffix
        var lang = LTAI.Core.I18n.Locale.IsChinese ? "zh" : "en";
        var langKey = $"{name}-{lang}";
        if (_cache.TryGetValue(langKey, out var cached))
            return cached;

        var langPath = Path.Combine(PromptsDir, langKey + ".prompt.md");
        if (File.Exists(langPath))
        {
            var content = File.ReadAllText(langPath);
            _cache[langKey] = content;
            return content;
        }

        // Fallback: try without language suffix
        if (_cache.TryGetValue(name, out var fallback))
            return fallback;

        var path = Path.Combine(PromptsDir, name + ".prompt.md");
        if (!File.Exists(path))
            return "";

        var fallbackContent = File.ReadAllText(path);
        _cache[name] = fallbackContent;
        return fallbackContent;
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
