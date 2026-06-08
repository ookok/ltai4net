using System.Text.Json;
using Spectre.Console;

namespace LTAI.TUI.Services;

public enum ThemeMode { Dark, Light }

public static class ThemeService
{
    public static bool IsLight { get; private set; }
    public static string? Language { get; set; }

    private static string PrefsPath =>
        Path.Combine(Environment.CurrentDirectory, ".livingtree", "preferences.json");

    static ThemeService()
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
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("theme", out var t))
                IsLight = string.Equals(t.GetString(), "light", StringComparison.OrdinalIgnoreCase);
            if (root.TryGetProperty("language", out var l))
                Language = l.GetString();
        }
        catch { /* ignore corrupt prefs */ }

        var env = Environment.GetEnvironmentVariable("LTAI_THEME");
        if (!string.IsNullOrEmpty(env))
            IsLight = string.Equals(env, "light", StringComparison.OrdinalIgnoreCase);

        ApplyToConsole();
    }

    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(PrefsPath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var prefs = new { theme = IsLight ? "light" : "dark", language = Language ?? "zh-CN" };
            File.WriteAllText(PrefsPath, JsonSerializer.Serialize(prefs));
        }
        catch { /* best-effort persist */ }
    }

    public static void Toggle()
    {
        IsLight = !IsLight;
        ApplyToConsole();
        Save();
    }

    public static void ApplyToConsole()
    {
        if (IsLight)
            AnsiConsole.Background = Color.White;
        else
            AnsiConsole.Background = Color.Default;
    }

    // ═══════════════════════════════════════════
    //  Semantic Color Palette — Dark / Light
    // ═══════════════════════════════════════════

    public static Color UserColor => IsLight ? Color.Blue : Color.Cyan;
    public static Color AssistantColor => IsLight ? Color.Green : Color.Green;
    public static Color ToolColor => IsLight ? Color.Navy : Color.Blue;
    public static Color ErrorColor => Color.Red;
    public static Color WarningColor => IsLight ? Color.Orange3 : Color.Yellow;
    public static Color SystemColor => IsLight ? Color.Orange3 : Color.Yellow;
    public static Color MutedColor => IsLight ? Color.Silver : Color.Grey;
    public static Color BorderColor => IsLight ? Color.Grey : Color.Grey42;
    public static Color AccentColor => IsLight ? Color.Teal : Color.Green;
    public static Color PrimaryColor => IsLight ? Color.Blue : Color.Cyan;
    public static Color CodeColor => IsLight ? Color.Grey : Color.Green;
    public static Color SurfaceColor => IsLight ? Color.Default : Color.Default;
    public static Color CodeBackgroundColor => IsLight ? Color.Default : Color.Default;

    // ═══════════════════════════════════════════
    //  Markup tag strings (for Spectre.Console markup interpolation)
    // ═══════════════════════════════════════════

    public static string UserTag => IsLight ? "blue" : "cyan";
    public static string AssistantTag => IsLight ? "green" : "green";
    public static string ToolTag => IsLight ? "navy" : "blue";
    public static string ErrorTag => "red";
    public static string WarningTag => IsLight ? "orange3" : "yellow";
    public static string SystemTag => IsLight ? "orange3" : "yellow";
    public static string MutedTag => IsLight ? "silver" : "grey";
    public static string BorderTag => IsLight ? "grey" : "grey42";
    public static string AccentTag => IsLight ? "teal" : "green";
    public static string PrimaryTag => IsLight ? "blue" : "cyan";
    public static string CodeTag => IsLight ? "grey" : "green";

    // ═══════════════════════════════════════════
    //  Legacy compatibility properties
    // ═══════════════════════════════════════════

    public static Color Primary => PrimaryColor;
    public static Color Accent => AccentColor;
    public static Color Warning => WarningColor;
    public static Color Error => ErrorColor;
    public static Color Muted => MutedColor;
    public static Color Border => BorderColor;

    public static Style PrimaryStyle => new(PrimaryColor);
    public static Style AccentStyle => new(AccentColor);
    public static Style MutedStyle => new(MutedColor);
    public static Style BorderStyle => new(BorderColor);
}
