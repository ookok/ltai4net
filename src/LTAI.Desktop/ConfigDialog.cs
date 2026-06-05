using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LTAI.Core.Configuration;

namespace LTAI.Desktop;

public sealed class ConfigDialog : Window
{
    private static readonly System.Net.Http.HttpClient _sharedHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly TextBlock _content;
    private readonly ComboBox _l1ModelBox;
    private readonly ComboBox _l2ModelBox;
    private readonly TextBlock _statusBar;

    public ConfigDialog()
    {
        Title = "配置管理";
        Width = 480;
        Height = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);

        var root = new StackPanel { Spacing = 8, Margin = new(16) };

        root.Children.Add(new TextBlock
        { Text = "配置管理", FontSize = 18, FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) });

        _content = new TextBlock
        { Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(_content);

        root.Children.Add(new TextBlock
        { Text = "L1 (Flash) 模型:", FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary), Margin = new(0, 8, 0, 0) });
        _l1ModelBox = new ComboBox { MinWidth = 300, Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) };
        root.Children.Add(_l1ModelBox);

        root.Children.Add(new TextBlock
        { Text = "L2 (Pro) 模型 (可选项，未配置时由 L1 替代):", FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary), Margin = new(0, 4, 0, 0) });
        _l2ModelBox = new ComboBox { MinWidth = 300, Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) };
        root.Children.Add(_l2ModelBox);

        var fetchBtn = new Button
        { Content = "🔄 获取可用模型", Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent), Margin = new(0, 4, 0, 0) };
        fetchBtn.Click += async (_, _) => await FetchModels();
        root.Children.Add(fetchBtn);

        _statusBar = new TextBlock
        { Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary), FontSize = 11 };
        root.Children.Add(_statusBar);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 4, 0, 0) };

        var clearAllBtn = new Button
        { Content = "🗑 清除所有 Key", Background = LtaiTheme.Sbb(LtaiTheme.AccentWarning),
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextOnAccent) };
        clearAllBtn.Click += (_, _) =>
        {
            var known = KnownKeys.All.Select(k => k.EnvVar).Distinct()
                .Where(e => !string.IsNullOrEmpty(e) && SecretManager.Has(e)).ToList();
            if (known.Count == 0) { _statusBar.Text = "当前没有任何已设置的 Key"; return; }
            foreach (var envVar in known)
            {
                SecretManager.Set(envVar, null, persistent: true);
                SecretManager.Invalidate(envVar);
            }
            RefreshKeys();
            _statusBar.Text = $"✅ 已清除 {known.Count} 个 Key";
        };
        btnRow.Children.Add(clearAllBtn);

        var closeBtn = new Button
        { Content = "关闭", Width = 80,
          Background = LtaiTheme.Sbb(LtaiTheme.BgPanel),
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary),
          BorderBrush = LtaiTheme.Sbb(LtaiTheme.Border) };
        closeBtn.Click += (_, _) => Close();
        btnRow.Children.Add(closeBtn);
        root.Children.Add(btnRow);

        Content = root;
        RefreshKeys();
    }

    private void RefreshKeys()
    {
        var lines = new System.Collections.Generic.List<string>();
        foreach (var k in KnownKeys.All)
        {
            var hasKey = SecretManager.Has(k.EnvVar);
            var status = hasKey ? "🟢" : "⚪";
            lines.Add($"{status} {k.EnvVar} — {k.Service}");
        }
        _content.Text = string.Join("\n", lines);
    }

    private async System.Threading.Tasks.Task FetchModels()
    {
        _statusBar.Text = "正在获取模型列表...";
        try
        {
            var defaultProvider = "DEEPSEEK_API_KEY";
            var apiKey = SecretManager.Get(defaultProvider);
            if (string.IsNullOrEmpty(apiKey))
                apiKey = KnownKeys.All.Select(k => SecretManager.Get(k.EnvVar))
                    .FirstOrDefault(k => !string.IsNullOrEmpty(k));
            if (string.IsNullOrEmpty(apiKey))
            { _statusBar.Text = "⚠️ 未配置 API Key"; return; }

            var keyInfo = KnownKeys.All.FirstOrDefault(k => SecretManager.Has(k.EnvVar));
            var endpoint = keyInfo?.Endpoint ?? "https://api.deepseek.com/v1";

            _sharedHttp.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
            using var resp = await _sharedHttp.GetAsync($"{endpoint.TrimEnd('/')}/models").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            { _statusBar.Text = $"⚠️ API 返回 {(int)resp.StatusCode}"; return; }

            using var json = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStreamAsync().ConfigureAwait(false));
            var models = json.RootElement.GetProperty("data")
                .EnumerateArray()
                .Select(m => m.GetProperty("id").GetString() ?? "")
                .Where(id => !string.IsNullOrEmpty(id))
                .OrderBy(id => id)
                .ToList();

            _l1ModelBox.ItemsSource = models;
            _l2ModelBox.ItemsSource = models;
            if (models.Count > 0) { _l1ModelBox.SelectedIndex = 0; if (models.Count > 1) _l2ModelBox.SelectedIndex = 1; }
            _statusBar.Text = $"✅ 获取到 {models.Count} 个模型";
        }
        catch (System.Exception ex) { _statusBar.Text = $"❌ 失败: {ex.Message}"; }
    }
}
