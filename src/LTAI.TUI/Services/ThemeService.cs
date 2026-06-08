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

        // Env var overrides persisted value
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

    public static Color Primary => IsLight ? Color.Blue : Color.Cyan;
    public static Color Accent => IsLight ? Color.Teal : Color.Green;
    public static Color Warning => Color.Yellow;
    public static Color Error => Color.Red;
    public static Color Muted => IsLight ? Color.Silver : Color.Grey;
    public static Color Border => IsLight ? Color.Grey : Color.Grey42;

    public static Style PrimaryStyle => new(Primary);
    public static Style AccentStyle => new(Accent);
    public static Style MutedStyle => new(Muted);
    public static Style BorderStyle => new(Border);
}
