using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LTAI.Desktop.ViewModels;

public sealed partial class SkillsViewModel : ViewModelBase
{
    public ObservableCollection<SkillItem> Skills { get; } = new();

    [ObservableProperty]
    private string _statusText = "";

    public sealed record SkillItem(string Name, string Description, string Path, DateTime LastUsed);

    public SkillsViewModel(string? skillsDir = null)
    {
        Refresh(skillsDir ?? Path.Combine(AppContext.BaseDirectory, "skills"));
    }

    public void Refresh(string skillsDir)
    {
        Skills.Clear();
        if (!Directory.Exists(skillsDir))
        {
            StatusText = "技能目录不存在，请先安装技能";
            return;
        }

        var usage = LoadUsage();
        foreach (var file in Directory.GetFiles(skillsDir, "*.md", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var relPath = Path.GetRelativePath(skillsDir, file);
            var desc = GetDesc(file);
            usage.TryGetValue(relPath, out var lastUsed);
            Skills.Add(new SkillItem(name, desc, relPath, lastUsed));
        }
        StatusText = $"共 {Skills.Count} 个技能";
    }

    private static string GetDesc(string path)
    {
        try
        {
            var firstLine = File.ReadLines(path).FirstOrDefault() ?? "";
            return firstLine.TrimStart('#', ' ', '-').Trim();
        }
        catch { return ""; }
    }

    private Dictionary<string, DateTime> LoadUsage()
    {
        try
        {
            var usageFile = Path.Combine(AppContext.BaseDirectory, ".livingtree", "skill-usage.json");
            if (!File.Exists(usageFile)) return new();
            var json = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, DateTime>>(
                File.ReadAllText(usageFile));
            return json ?? new();
        }
        catch { return new(); }
    }
}
