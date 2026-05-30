using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LTAI.Desktop;

/// <summary>
/// 技能管理面板 — Desktop 版，显示已加载技能和遗忘操作
/// </summary>
public sealed class SkillsView : UserControl
{
    private readonly TextBlock _content;
    private readonly string _usageFile;

    public SkillsView(string? skillsDir = null)
    {
        _usageFile = Path.Combine(Directory.GetCurrentDirectory(), ".livingtree", "skill_usage.json");
        var rootDir = skillsDir ?? Path.Combine(Directory.GetCurrentDirectory(), "skills");

        Background = LtaiTheme.Sbb(LtaiTheme.Bg);
        var outer = new StackPanel { Spacing = 8, Margin = new(16) };
        outer.Children.Add(new TextBlock
        {
            Text = "技能管理",
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary)
        });

        _content = new TextBlock
        {
            Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary),
            TextWrapping = TextWrapping.Wrap,
        };
        outer.Children.Add(_content);
        Content = outer;
        RefreshView(rootDir);
    }

    private void RefreshView(string skillsDir)
    {
        if (!Directory.Exists(skillsDir))
        {
            _content.Text = "技能目录不存在: " + skillsDir;
            return;
        }

        var usage = LoadUsage();
        var lines = new System.Collections.Generic.List<string>();

        foreach (var sub in Directory.GetDirectories(skillsDir).OrderBy(Path.GetFileName))
        {
            var md = Path.Combine(sub, "SKILL.md");
            if (!File.Exists(md)) continue;
            var name = Path.GetFileName(sub);
            var desc = GetDesc(md);
            var lastUsed = usage.TryGetValue(name, out var dt) ? dt : (DateTime?)null;
            var expired = lastUsed.HasValue && (DateTime.UtcNow - lastUsed.Value) > TimeSpan.FromDays(30);
            var status = !lastUsed.HasValue ? "⚪ 未使用"
                       : expired ? "🔴 已过期"
                       : "🟢 活跃";
            var time = lastUsed?.ToString("yyyy-MM-dd") ?? "从未";
            lines.Add($"{status}  {name} — {desc}");
            lines.Add($"  上次使用: {time}");
            lines.Add("");
        }

        _content.Text = string.Join("\n", lines);
    }

    private static string GetDesc(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            if (text.StartsWith("---\n") || text.StartsWith("---\r\n"))
            {
                var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
                if (end > 0)
                    foreach (var l in text[4..end].Split('\n'))
                        if (l.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                            return l[12..].Trim().Trim('"').Trim('\'');
            }
        }
        catch
        {
            // 非关键：技能文件解析失败时返回空
        }
        return "";
    }

    private System.Collections.Generic.Dictionary<string, DateTime> LoadUsage()
    {
        if (!File.Exists(_usageFile)) return new();
        try { return JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, DateTime>>(File.ReadAllText(_usageFile)) ?? new(); }
        catch { return new(); }
    }
}
