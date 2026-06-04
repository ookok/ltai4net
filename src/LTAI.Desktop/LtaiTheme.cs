using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media;

namespace LTAI.Desktop;

public enum AppTheme { Dark, Light }

public static class LtaiTheme
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static event Action? ThemeChanged;

    private static readonly ConcurrentDictionary<(Color Color, byte Alpha), SolidColorBrush> _brushCache = new();

    public static void Toggle()
    {
        Current = Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        _brushCache.Clear();
        ThemeChanged?.Invoke();
    }

    // ─── Background / Surface ───
    public static Color Bg             => Current == AppTheme.Dark ? Color.Parse("#0f172a") : Color.Parse("#ffffff");
    public static Color BgPanel        => Current == AppTheme.Dark ? Color.Parse("#1e293b") : Color.Parse("#f1f5f9");
    public static Color BgInput        => Current == AppTheme.Dark ? Color.Parse("#0f172a") : Color.Parse("#ffffff");
    public static Color SurfaceOverlay => Current == AppTheme.Dark ? Color.Parse("#1a2332") : Color.Parse("#e2e8f0");

    // ─── Borders ───
    public static Color Border         => Current == AppTheme.Dark ? Color.Parse("#334155") : Color.Parse("#cbd5e1");
    public static Color CodeBorder     => Current == AppTheme.Dark ? Color.Parse("#1e293b") : Color.Parse("#cbd5e1");
    public static Color CurrentLineBorder => Current == AppTheme.Dark ? Color.Parse("#334155") : Color.Parse("#cbd5e1");

    // ─── Text ───
    public static Color TextPrimary    => Current == AppTheme.Dark ? Color.Parse("#e2e8f0") : Color.Parse("#0f172a");
    public static Color TextSecondary  => Current == AppTheme.Dark ? Color.Parse("#94a3b8") : Color.Parse("#64748b");
    public static Color TextDim        => Current == AppTheme.Dark ? Color.Parse("#64748b") : Color.Parse("#94a3b8");
    public static Color TextMuted      => Current == AppTheme.Dark ? Color.Parse("#475569") : Color.Parse("#64748b");
    public static Color TextOnAccent   => Current == AppTheme.Dark ? Color.Parse("#ffffff") : Color.Parse("#ffffff");
    public static Color TextOnBubble   => Current == AppTheme.Dark ? Color.Parse("#e2e8f0") : Color.Parse("#0f172a");

    // ─── Accents ───
    public static Color AccentDNA      => Current == AppTheme.Dark ? Color.Parse("#58a6ff") : Color.Parse("#0969da");
    public static Color AccentSystem   => Current == AppTheme.Dark ? Color.Parse("#3fb950") : Color.Parse("#1a7f37");
    public static Color AccentWarning  => Current == AppTheme.Dark ? Color.Parse("#d29922") : Color.Parse("#9a6700");
    public static Color AccentDanger   => Current == AppTheme.Dark ? Color.Parse("#f85149") : Color.Parse("#cf222e");
    public static Color AccentInfo     => Current == AppTheme.Dark ? Color.Parse("#a371f7") : Color.Parse("#8250df");

    // ─── Chat Bubbles ───
    public static Color BubbleUserBg   => Current == AppTheme.Dark ? Color.Parse("#1e40af") : Color.Parse("#dbeafe");
    public static Color BubbleUserBorder => Current == AppTheme.Dark ? Color.Parse("#1e40af") : Color.Parse("#93c5fd");
    public static Color BubbleAIBg     => Current == AppTheme.Dark ? Color.Parse("#1e293b") : Color.Parse("#f1f5f9");
    public static Color BubbleAIBorder => Current == AppTheme.Dark ? Color.Parse("#334155") : Color.Parse("#cbd5e1");

    // ─── Code / Editor ───
    public static Color CodeBg         => Current == AppTheme.Dark ? Color.Parse("#0f172a") : Color.Parse("#f1f5f9");
    public static Color SelectionBg    => Current == AppTheme.Dark ? Color.Parse("#264f78") : Color.Parse("#d0d7de");
    public static Color DiffGreen      => Current == AppTheme.Dark ? Color.Parse("#4CAF50") : Color.Parse("#1a7f37");
    public static Color DiffRed        => Current == AppTheme.Dark ? Color.Parse("#F44336") : Color.Parse("#cf222e");

    // ─── Misc ───
    // ─── Fonts ───
    public static FontFamily CodeFont => new("JetBrains Mono, Fira Code, Consolas, monospace");

    // ─── Corner Radius ───
    public static class Radius
    {
        public static CornerRadius Sm => new(6);   // buttons, tags, compact cards
        public static CornerRadius Md => new(8);   // inputs, code blocks, panels
        public static CornerRadius Lg => new(12);  // message bubbles
        public static CornerRadius Xl => new(16);  // dialogs, modals
    }

    public static Color ThinkBg        => Current == AppTheme.Dark ? Color.Parse("#1a1b2f") : Color.Parse("#f0f0ff");
    public static Color ToolBg         => Current == AppTheme.Dark ? Color.Parse("#1b2a1b") : Color.Parse("#f0fff0");
    public static Color ChatUser       => Current == AppTheme.Dark ? Color.Parse("#58a6ff") : Color.Parse("#0969da");
    public static Color ChatAI         => Current == AppTheme.Dark ? Color.Parse("#3fb950") : Color.Parse("#1a7f37");

    public static SolidColorBrush Sbb(Color c) => _brushCache.GetOrAdd((c, (byte)255), static key => new SolidColorBrush(key.Color));
    public static SolidColorBrush Sbb(Color c, byte alpha) => _brushCache.GetOrAdd((c, alpha), key => new SolidColorBrush(key.Color) { Opacity = key.Alpha / 255.0 });
    public static SolidColorBrush Sbb(string hex) => Sbb(Color.Parse(hex), 255);
    public static SolidColorBrush Sbb(string hex, byte alpha) => Sbb(Color.Parse(hex), alpha);
}
