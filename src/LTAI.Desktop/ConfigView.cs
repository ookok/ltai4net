using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LTAI.Core.Configuration;

namespace LTAI.Desktop;

/// <summary>
/// 配置面板：Provider/Key 管理 + L1/L2 模型选择
/// </summary>
public sealed class ConfigView : UserControl
{
    private static readonly System.Net.Http.HttpClient _sharedHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly TextBlock _content;
    private readonly ComboBox _l1ModelBox;
    private readonly ComboBox _l2ModelBox;
    private readonly TextBlock _statusBar;

    public ConfigView()
    {
        Background = LtaiTheme.Sbb(LtaiTheme.Bg);
        var root = new StackPanel { Spacing = 8, Margin = new(16) };

        root.Children.Add(new TextBlock
        { Text = "配置管理", FontSize = 18, FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) });

        _content = new TextBlock
        { Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(_content);

        // L1/L2 模型选择
        root.Children.Add(new TextBlock
        { Text = "L1 (Flash) 模型:", FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary), Margin = new(0, 8, 0, 0) });
        _l1ModelBox = new ComboBox { MinWidth = 300, Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) };
        root.Children.Add(_l1ModelBox);

        root.Children.Add(new TextBlock
        { Text = "L2 (Pro) 模型:", FontWeight = FontWeight.Bold,
          Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary), Margin = new(0, 4, 0, 0) });
        _l2ModelBox = new ComboBox { MinWidth = 300, Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary) };
        root.Children.Add(_l2ModelBox);

        var fetchBtn = new Button
        { Content = "🔄 获取可用模型", Background = LtaiTheme.Sbb(LtaiTheme.AccentDNA),
          Foreground = LtaiTheme.Sbb("#ffffff"), Margin = new(0, 4, 0, 0) };
        fetchBtn.Click += async (_, _) => await FetchModels();
        root.Children.Add(fetchBtn);

        _statusBar = new TextBlock
        { Foreground = LtaiTheme.Sbb(LtaiTheme.TextSecondary), FontSize = 11 };
        root.Children.Add(_statusBar);

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
            {
                // Try other known provider keys
                apiKey = KnownKeys.All.Select(k => SecretManager.Get(k.EnvVar))
                    .FirstOrDefault(k => !string.IsNullOrEmpty(k));
            }
            if (string.IsNullOrEmpty(apiKey))
            { _statusBar.Text = "⚠️ 未配置 API Key"; return; }

            // Find the default provider endpoint
            var keyInfo = KnownKeys.All.FirstOrDefault(k => SecretManager.Has(k.EnvVar));
            var endpoint = keyInfo?.Endpoint ?? "https://api.deepseek.com/v1";

            _sharedHttp.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
            using var resp = await _sharedHttp.GetAsync($"{endpoint.TrimEnd('/')}/models");
            if (!resp.IsSuccessStatusCode)
            { _statusBar.Text = $"⚠️ API 返回 {(int)resp.StatusCode}"; return; }

            using var json = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
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
