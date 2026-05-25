using Avalonia.Media;

namespace LTAI.Desktop;

public enum AppTheme { Dark, Light }

public static class LtaiTheme
{
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static event Action? ThemeChanged;

    public static void Toggle()
    {
        Current = Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        ThemeChanged?.Invoke();
    }

    public static Color Bg         => Current == AppTheme.Dark ? Color.Parse("#0d1117") : Color.Parse("#ffffff");
    public static Color BgPanel    => Current == AppTheme.Dark ? Color.Parse("#161b22") : Color.Parse("#f6f8fa");
    public static Color BgInput    => Current == AppTheme.Dark ? Color.Parse("#0d1117") : Color.Parse("#ffffff");
    public static Color Border     => Current == AppTheme.Dark ? Color.Parse("#30363d") : Color.Parse("#d0d7de");
    public static Color TextPrimary  => Current == AppTheme.Dark ? Color.Parse("#e6edf3") : Color.Parse("#1f2328");
    public static Color TextSecondary => Current == AppTheme.Dark ? Color.Parse("#8b949e") : Color.Parse("#656d76");
    public static Color TextDim     => Current == AppTheme.Dark ? Color.Parse("#484f58") : Color.Parse("#8b949e");
    public static Color AccentDNA   => Current == AppTheme.Dark ? Color.Parse("#58a6ff") : Color.Parse("#0969da");
    public static Color AccentSystem => Current == AppTheme.Dark ? Color.Parse("#3fb950") : Color.Parse("#1a7f37");
    public static Color AccentWarning => Current == AppTheme.Dark ? Color.Parse("#d29922") : Color.Parse("#9a6700");
    public static Color AccentDanger  => Current == AppTheme.Dark ? Color.Parse("#f85149") : Color.Parse("#cf222e");
    public static Color AccentInfo    => Current == AppTheme.Dark ? Color.Parse("#a371f7") : Color.Parse("#8250df");
    public static Color CodeBg     => Current == AppTheme.Dark ? Color.Parse("#0d1117") : Color.Parse("#f6f8fa");
    public static Color CodeBorder => Current == AppTheme.Dark ? Color.Parse("#21262d") : Color.Parse("#d0d7de");
    public static Color ThinkBg    => Current == AppTheme.Dark ? Color.Parse("#1a1b2f") : Color.Parse("#f0f0ff");
    public static Color ToolBg     => Current == AppTheme.Dark ? Color.Parse("#1b2a1b") : Color.Parse("#f0fff0");
    public static Color ChatUser   => Current == AppTheme.Dark ? Color.Parse("#58a6ff") : Color.Parse("#0969da");
    public static Color ChatAI     => Current == AppTheme.Dark ? Color.Parse("#3fb950") : Color.Parse("#1a7f37");

    public static SolidColorBrush Sbb(Color c) => new(c);
    public static SolidColorBrush Sbb(string hex) => new(Color.Parse(hex));
}
