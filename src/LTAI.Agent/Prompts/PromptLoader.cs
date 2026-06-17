using System.Collections.Concurrent;
using LTAI.Core.I18n;

namespace LTAI.Agent.Prompts;

/// <summary>
/// Loads prompt files from agents/*.prompt.md. Supports bilingual (zh/en) resolution
/// with <see cref="FileSystemWatcher"/>-based cache invalidation.
///
/// Static shims preserve backward compatibility for callers in static classes.
/// Prefer DI injection of <see cref="IPromptLoader"/> in new code.
/// </summary>
public sealed class PromptLoader : IPromptLoader, IDisposable
{
    private string? _promptsDir;
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private readonly FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <summary>Default lazy singleton used by static shims.</summary>
    private static readonly Lazy<PromptLoader> _default = new(() => new PromptLoader());

    // ═══════════════════════════════════════════
    //  Static shims (backward compatible)
    // ═══════════════════════════════════════════

    public static string Load(string name) => _default.Value.LoadCore(name);
    public static string LoadLang(string name) => _default.Value.LoadLangCore(name);
    public static void ClearCache() { if (_default.IsValueCreated) _default.Value.ClearCacheCore(); }

    // ═══════════════════════════════════════════
    //  Constructor & lifecycle
    // ═══════════════════════════════════════════

    public PromptLoader()
    {
        try
        {
            var dir = ResolvePromptsDir();
            if (dir != null && Directory.Exists(dir))
            {
                _promptsDir = dir;
                _watcher = new FileSystemWatcher(dir, "*.prompt.md")
                {
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.LastWrite
                };
                _watcher.Changed += (_, _) => _cache.Clear();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("PromptLoader: FileSystemWatcher init failed: {0}", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher?.Dispose();
        _cache.Clear();
    }

    // ═══════════════════════════════════════════
    //  Instance implementation (IPromptLoader)
    // ═══════════════════════════════════════════

    string IPromptLoader.Load(string name) => LoadCore(name);
    string IPromptLoader.LoadLang(string name) => LoadLangCore(name);
    void IPromptLoader.ClearCache() => ClearCacheCore();

    // ═══════════════════════════════════════════
    //  Core logic
    // ═══════════════════════════════════════════

    private string LoadCore(string name)
    {
        var lang = Locale.IsChinese ? "zh" : "en";
        var langKey = $"{name}-{lang}";
        if (_cache.TryGetValue(langKey, out var cached))
            return cached;

        var dir = _promptsDir ?? ResolvePromptsDir();
        var langPath = Path.Combine(dir, langKey + ".prompt.md");
        if (File.Exists(langPath))
        {
            var content = File.ReadAllText(langPath);
            _cache[langKey] = content;
            return content;
        }

        if (_cache.TryGetValue(name, out var fallback))
            return fallback;

        var path = Path.Combine(dir, name + ".prompt.md");
        if (!File.Exists(path))
        {
            System.Diagnostics.Trace.TraceWarning(
                "PromptLoader: file not found \"{Path}\" for name \"{Name}\" — check that agents/ directory exists and contains *.prompt.md files",
                path, name);
            return "";
        }

        var fallbackContent = File.ReadAllText(path);
        _cache[name] = fallbackContent;
        return fallbackContent;
    }

    private string LoadLangCore(string name)
    {
        var zh = LoadCore($"{name}-zh");
        if (!string.IsNullOrEmpty(zh) && Locale.IsChinese)
            return zh;

        var en = LoadCore($"{name}-en");
        if (!string.IsNullOrEmpty(en))
            return en;

        return zh;
    }

    private void ClearCacheCore() => _cache.Clear();

    private static string ResolvePromptsDir()
    {
        var dir = AppContext.BaseDirectory ?? Directory.GetCurrentDirectory();
        while (dir != null && !Directory.Exists(Path.Combine(dir, "agents")))
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return Path.Combine(dir ?? AppContext.BaseDirectory ?? "", "agents");
    }
}
