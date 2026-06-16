using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LTAI.Desktop;

public enum AppTheme { Dark, Light }

public static class LtaiTheme
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static event Action? ThemeChanged;

    private static readonly ConcurrentDictionary<(Color Color, byte Alpha), SolidColorBrush> _brushCache = new();
    private static ResourceDictionary? _resourceDictionary;

    public static ResourceDictionary GetResources()
    {
        if (_resourceDictionary == null)
        {
            _resourceDictionary = [];
            PopulateResources();
            ThemeChanged += () => { _resourceDictionary.Clear(); PopulateResources(); };
        }
        return _resourceDictionary;
    }

    private static void PopulateResources()
    {
        if (_resourceDictionary == null) return;
        _resourceDictionary["LtaiBg"] = Sbb(Bg);
        _resourceDictionary["LtaiBgPanel"] = Sbb(BgPanel);
        _resourceDictionary["LtaiBorder"] = Sbb(Border);
        _resourceDictionary["LtaiTextPrimary"] = Sbb(TextPrimary);
        _resourceDictionary["LtaiTextDim"] = Sbb(TextDim);
        _resourceDictionary["LtaiTextMuted"] = Sbb(TextMuted);
        _resourceDictionary["LtaiAccentDNA"] = Sbb(AccentDNA);
        _resourceDictionary["LtaiAccentSystem"] = Sbb(AccentSystem);
        _resourceDictionary["LtaiAccentDanger"] = Sbb(AccentDanger);
        _resourceDictionary["LtaiAccentWarning"] = Sbb(AccentWarning);
        _resourceDictionary["LtaiAccentInfo"] = Sbb(AccentInfo);
        _resourceDictionary["LtaiBubbleUserBg"] = Sbb(BubbleUserBg);
        _resourceDictionary["LtaiBubbleAIBg"] = Sbb(BubbleAIBg);
        _resourceDictionary["LtaiCodeBg"] = Sbb(CodeBg);
        _resourceDictionary["LtaiCodeFont"] = CodeFont;
    }

    private static string PrefsPath =>
        Path.Combine(AppContext.BaseDirectory, ".livingtree", "preferences.json");

    static LtaiTheme()
    {
        Load();
    }

    public static void Load()
    {
        try
        {
            var path = PrefsPath;
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("theme", out var t) &&
                string.Equals(t.GetString(), "light", StringComparison.OrdinalIgnoreCase))
                Current = AppTheme.Light;
        }
        catch
        {
            // non-critical, best-effort
        }
    }

    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(PrefsPath);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var prefs = new { theme = Current == AppTheme.Light ? "light" : "dark" };
            File.WriteAllText(PrefsPath, JsonSerializer.Serialize(prefs));
        }
        catch
        {
            // non-critical, best-effort
        }
    }

    public static void Toggle()
    {
        Current = Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        _brushCache.Clear();
        Save();
        var handler = ThemeChanged;
        handler?.Invoke();
    }

    // ─── Apple-style palette ───
    public static Color Bg             => Current == AppTheme.Dark ? Color.Parse("#1c1c1e") : Color.Parse("#f5f5f7");
    public static Color BgPanel        => Current == AppTheme.Dark ? Color.Parse("#2c2c2e") : Color.Parse("#ffffff");
    public static Color BgInput        => Current == AppTheme.Dark ? Color.Parse("#1c1c1e") : Color.Parse("#f5f5f7");
    public static Color SurfaceOverlay => Current == AppTheme.Dark ? Color.Parse("#3a3a3c") : Color.Parse("#e8e8ed");

    public static Color Border         => Current == AppTheme.Dark ? Color.Parse("#3a3a3c") : Color.Parse("#d2d2d7");
    public static Color CodeBorder     => Current == AppTheme.Dark ? Color.Parse("#3a3a3c") : Color.Parse("#d2d2d7");
    public static Color CurrentLineBorder => Current == AppTheme.Dark ? Color.Parse("#3a3a3c") : Color.Parse("#d2d2d7");

    public static Color TextPrimary    => Current == AppTheme.Dark ? Color.Parse("#f5f5f7") : Color.Parse("#1d1d1f");
    public static Color TextSecondary  => Current == AppTheme.Dark ? Color.Parse("#a1a1a6") : Color.Parse("#86868b");
    public static Color TextDim        => Current == AppTheme.Dark ? Color.Parse("#636366") : Color.Parse("#aeaeb2");
    public static Color TextMuted      => Current == AppTheme.Dark ? Color.Parse("#48484a") : Color.Parse("#c7c7cc");
    public static Color TextOnAccent   => Color.Parse("#ffffff");
    public static Color TextOnBubble   => Current == AppTheme.Dark ? Color.Parse("#f5f5f7") : Color.Parse("#1d1d1f");

    public static Color AccentDNA      => Current == AppTheme.Dark ? Color.Parse("#0a84ff") : Color.Parse("#0071e3");
    public static Color AccentSystem   => Current == AppTheme.Dark ? Color.Parse("#30d158") : Color.Parse("#34c759");
    public static Color AccentWarning  => Current == AppTheme.Dark ? Color.Parse("#ffd60a") : Color.Parse("#ff9f0a");
    public static Color AccentDanger   => Current == AppTheme.Dark ? Color.Parse("#ff453a") : Color.Parse("#ff3b30");
    public static Color AccentInfo     => Current == AppTheme.Dark ? Color.Parse("#bf5af2") : Color.Parse("#af52de");

    public static Color BubbleUserBg   => Current == AppTheme.Dark ? Color.Parse("#0a84ff") : Color.Parse("#0071e3");
    public static Color BubbleUserBorder => Current == AppTheme.Dark ? Color.Parse("#0a84ff") : Color.Parse("#0071e3");
    public static Color BubbleAIBg     => Current == AppTheme.Dark ? Color.Parse("#2c2c2e") : Color.Parse("#e8e8ed");
    public static Color BubbleAIBorder => Current == AppTheme.Dark ? Color.Parse("#3a3a3c") : Color.Parse("#d2d2d7");

    public static Color CodeBg         => Current == AppTheme.Dark ? Color.Parse("#2c2c2e") : Color.Parse("#f5f5f7");
    public static Color SelectionBg    => Current == AppTheme.Dark ? Color.Parse("#264f78") : Color.Parse("#d0d7de");
    public static Color DiffGreen      => Current == AppTheme.Dark ? Color.Parse("#30d158") : Color.Parse("#34c759");
    public static Color DiffRed        => Current == AppTheme.Dark ? Color.Parse("#ff453a") : Color.Parse("#ff3b30");

    public static FontFamily CodeFont => new("SF Mono, JetBrains Mono, Fira Code, monospace");

    public static class Radius
    {
        public static CornerRadius Sm => new(6);
        public static CornerRadius Md => new(10);
        public static CornerRadius Lg => new(14);
        public static CornerRadius Xl => new(18);
    }

    public static Color ThinkBg        => Current == AppTheme.Dark ? Color.Parse("#2c2c3a") : Color.Parse("#f0f0ff");
    public static Color ToolBg         => Current == AppTheme.Dark ? Color.Parse("#2c3a2c") : Color.Parse("#f0fff0");
    public static Color ChatUser       => Current == AppTheme.Dark ? Color.Parse("#0a84ff") : Color.Parse("#0071e3");
    public static Color ChatAI         => Current == AppTheme.Dark ? Color.Parse("#30d158") : Color.Parse("#34c759");

    public static SolidColorBrush Sbb(Color c) => _brushCache.GetOrAdd((c, (byte)255), static key => new SolidColorBrush(key.Color));
    public static SolidColorBrush Sbb(Color c, byte alpha) => _brushCache.GetOrAdd((c, alpha), key => new SolidColorBrush(key.Color) { Opacity = key.Alpha / 255.0 });
    public static SolidColorBrush Sbb(string hex) => Sbb(Color.Parse(hex), 255);
    public static SolidColorBrush Sbb(string hex, byte alpha) => Sbb(Color.Parse(hex), alpha);
}
