using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LTAI.Core.Configuration;

namespace LTAI.Desktop;

public sealed class ConfigDialog : Window
{
    private readonly ViewModels.ConfigViewModel _vm;
    private readonly TextBlock _content;
    private readonly TextBlock _statusBar;
    private readonly CheckBox _trustAllBox;
    private readonly TextBox _trustNamesBox;
    private readonly TextBlock _trustStatus;

    public ConfigDialog()
    {
        _vm = new ViewModels.ConfigViewModel();
        DataContext = _vm;
        Title = "配置管理";
        Width = 520;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var scroll = new ScrollViewer();
        var root = new StackPanel { Spacing = 8, Margin = new(16) };

        root.Children.Add(new TextBlock
        { Text = "配置管理", FontSize = 18, FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) });

        _content = new TextBlock
        { Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(_content);

        var l1Label = new TextBlock { Text = "L1 快速反应模型（长江苦力二号）:", FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary), Margin = new(0, 8, 0, 0) };
        root.Children.Add(l1Label);
        var l1Box = new ComboBox { MinWidth = 300, Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) };
        l1Box.Bind(ComboBox.ItemsSourceProperty, new Avalonia.Data.Binding(nameof(_vm.L1Models)));
        l1Box.Bind(ComboBox.SelectedItemProperty, new Avalonia.Data.Binding(nameof(_vm.SelectedL1Model)));
        root.Children.Add(l1Box);

        var l2Label = new TextBlock { Text = "L2 深度推理模型（长江苦力三号）:", FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary), Margin = new(0, 4, 0, 0) };
        root.Children.Add(l2Label);
        var l2Box = new ComboBox { MinWidth = 300, Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) };
        l2Box.Bind(ComboBox.ItemsSourceProperty, new Avalonia.Data.Binding(nameof(_vm.L2Models)));
        l2Box.Bind(ComboBox.SelectedItemProperty, new Avalonia.Data.Binding(nameof(_vm.SelectedL2Model)));
        root.Children.Add(l2Box);

        var fetchBtn = new Button
        { Content = "🔄 获取可用模型", Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent), Margin = new(0, 4, 0, 0) };
        fetchBtn.Click += (_, _) => _vm.FetchModelsCommand.Execute(null);
        root.Children.Add(fetchBtn);

        _statusBar = new TextBlock
        { Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary), FontSize = 11 };
        _statusBar.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(_vm.StatusBarText)));
        root.Children.Add(_statusBar);

        // ════════════════════════════════════════
        // 工具信任配置
        // ════════════════════════════════════════
        var sep = new Border { Height = 1, Background = LtaiTheme.Sbb(LtaiTheme.Border), Margin = new(0, 8, 0, 4) };
        root.Children.Add(sep);

        root.Children.Add(new TextBlock
        { Text = "工具信任（免确认执行）", FontSize = 15, FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) });

        _trustAllBox = new CheckBox
        { Content = "完全信任所有工具（TrustAll）", Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
          IsChecked = ReadTrustAll() };
        root.Children.Add(_trustAllBox);

        root.Children.Add(new TextBlock
        { Text = "信任工具列表（每行一个，支持通配符 * ）",
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary), FontSize = 12, Margin = new(0, 4, 0, 0) });
        root.Children.Add(new TextBlock
        { Text = "示例: SafeShellTool.RunCommand / CSharpScriptTool.* / *.RunCommand",
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextDim), FontSize = 11 });

        _trustNamesBox = new TextBox
        { MinHeight = 80, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
          Background = LtaiTheme.Sbb(LtaiTheme.BgInput),
          BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border),
          Text = string.Join("\n", ReadTrustedNames()) };
        root.Children.Add(_trustNamesBox);

        _trustStatus = new TextBlock
        { Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary), FontSize = 11 };
        root.Children.Add(_trustStatus);

        var saveTrustBtn = new Button
        { Content = "💾 保存信任配置", Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent), Margin = new(0, 4, 0, 0) };
        saveTrustBtn.Click += (_, _) => SaveTrustConfig();
        root.Children.Add(saveTrustBtn);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 8, 0, 0) };
        var clearAllBtn = new Button
        { Content = "🗑 清除所有 Key", Background = LtaiTheme.Sbb(LtaiTheme.AccentWarning),
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent) };
        clearAllBtn.Click += (_, _) => _vm.ClearAllKeysCommand.Execute(null);
        btnRow.Children.Add(clearAllBtn);

        var closeBtn = new Button
        { Content = "关闭", Width = 80, Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary), BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border) };
        closeBtn.Click += (_, _) => Close();
        btnRow.Children.Add(closeBtn);
        root.Children.Add(btnRow);

        scroll.Content = root;
        Content = scroll;
        RefreshKeys();
    }

    private void RefreshKeys()
    {
        var lines = new List<string>();
        foreach (var k in LTAI.Core.Configuration.KnownKeys.All)
        {
            var hasKey = LTAI.Core.Configuration.SecretManager.Has(k.EnvVar);
            lines.Add($"{(hasKey ? "🟢" : "⚪")} {k.EnvVar} — {k.Service}");
        }
        _content.Text = string.Join("\n", lines);
    }

    // ════════════════════════════════════════
    // 工具信任: 读写 appsettings.json
    // ════════════════════════════════════════

    private static string ConfigPath() =>
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    private static bool ReadTrustAll()
    {
        try
        {
            var path = ConfigPath();
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return doc.RootElement.TryGetProperty("LTAI", out var l)
                && l.TryGetProperty("ToolTrust", out var t)
                && t.TryGetProperty("TrustAll", out var a)
                && a.GetBoolean();
        }
        catch { return false; }
    }

    private static string[] ReadTrustedNames()
    {
        try
        {
            var path = ConfigPath();
            if (!File.Exists(path)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!doc.RootElement.TryGetProperty("LTAI", out var l)) return [];
            if (!l.TryGetProperty("ToolTrust", out var t)) return [];
            if (!t.TryGetProperty("TrustedToolNames", out var names)) return [];
            var result = new List<string>();
            foreach (var n in names.EnumerateArray())
            {
                var s = n.GetString();
                if (!string.IsNullOrEmpty(s)) result.Add(s);
            }
            return result.ToArray();
        }
        catch { return []; }
    }

    private void SaveTrustConfig()
    {
        try
        {
            var path = ConfigPath();
            JsonNode json;
            if (File.Exists(path))
                json = JsonNode.Parse(File.ReadAllText(path))!;
            else
                json = new JsonObject();

            var ltai = json["LTAI"] as JsonObject ?? new JsonObject();
            json["LTAI"] = ltai;
            var trust = ltai["ToolTrust"] as JsonObject ?? new JsonObject();
            ltai["ToolTrust"] = trust;

            trust["TrustAll"] = _trustAllBox.IsChecked == true;

            var names = _trustNamesBox.Text?
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? [];
            trust["TrustedToolNames"] = new JsonArray(names.Select(n => (JsonNode)n!).ToArray());

            File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            _trustStatus.Text = "✅ 信任配置已保存";
            _trustStatus.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentSystem);
        }
        catch (Exception ex)
        {
            _trustStatus.Text = $"❌ 保存失败: {ex.Message}";
            _trustStatus.Foreground = LtaiTheme.Sbb(LtaiTheme.AccentWarning);
        }
    }
}
