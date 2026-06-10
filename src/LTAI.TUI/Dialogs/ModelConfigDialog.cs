using System.Collections.ObjectModel;
using LTAI.Core.Configuration;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace LTAI.TUI.Dialogs;

/// <summary>Modal model config dialog — L0/L1/L2 layer tabs with provider/API key/model.</summary>
public sealed class ModelConfigDialog : Dialog
{
    private readonly IApplication _app;
    private readonly string _layer;
    private readonly DropDownList _providerDropdown;
    private readonly TextField _apiKeyField;
    private readonly DropDownList _modelDropdown;
    private readonly Label _statusLabel;

    private static readonly (string key, string label)[] Layers = { ("L1", "快速反应"), ("L2", "深度推理"), ("L0", "并行辅助") };
    private static readonly KnownKeys.KeyInfo[] Providers = KnownKeys.All;

    public ModelConfigDialog(IApplication app, string initialLayer)
    {
        _app = app;
        _layer = initialLayer;
        Title = "模型配置";
        Width = 58; Height = 24;
        X = Pos.Center(); Y = Pos.Center();

        // ── Layer tabs ──
        for (int i = 0; i < Layers.Length; i++)
        {
            var (key, label) = Layers[i];
            var isActive = key == _layer;
            var btn = new Button { X = i * 16, Y = 0, Text = isActive ? $"[{label}]" : $" {label} " };
            var cap = key;
            btn.Accepting += (_, _) => { LayerSwitchRequested = cap; _app.RequestStop(); };
            Add(btn);
        }

        var (currentProvider, currentModel) = LoadLayerConfig(_layer);
        var providerNames = Providers.Select(p => p.Service).ToList();

        // ── Provider DropDownList (带边框) ──
        Add(new Label { X = 1, Y = 2, Text = "Provider:" });
        var selIdx = providerNames.FindIndex(p => p.Equals(currentProvider, StringComparison.OrdinalIgnoreCase));
        if (selIdx < 0) selIdx = 0;
        _providerDropdown = new DropDownList
        {
            X = 0, Y = 0, Width = Dim.Fill(),
            Source = new ListWrapper<string>(new ObservableCollection<string>(providerNames)),
            ReadOnly = true,
            Text = providerNames[selIdx],
        };
        _providerDropdown.ValueChanged += OnProviderChanged;
        var providerFrame = new FrameView
        {
            X = 1, Y = 3, Width = 24, Height = 3,
        };
        providerFrame.Add(_providerDropdown);
        Add(providerFrame);

        // ── API Key (right panel) ──
        Add(new Label { X = 26, Y = 3, Text = "API Key:" });
        _apiKeyField = new TextField { X = 26, Y = 4, Width = 28, Secret = true };
        // Auto-fill API key for selected provider
        if (selIdx >= 0 && selIdx < Providers.Length)
        {
            var envVal = SecretManager.Get(Providers[selIdx].EnvVar);
            if (!string.IsNullOrEmpty(envVal)) _apiKeyField.Text = envVal;
        }
        Add(_apiKeyField);

        // ── Model DropDownList (带边框) ──
        Add(new Label { X = 26, Y = 5, Text = "Model:" });
        _modelDropdown = new DropDownList
        {
            X = 0, Y = 0, Width = Dim.Fill(),
            ReadOnly = true,
        };
        var modelItems = new ObservableCollection<string>();
        if (!string.IsNullOrEmpty(currentModel)) modelItems.Add(currentModel);
        _modelDropdown.Source = new ListWrapper<string>(modelItems);
        if (!string.IsNullOrEmpty(currentModel)) _modelDropdown.Text = currentModel;
        var modelFrame = new FrameView
        {
            X = 26, Y = 6, Width = 28, Height = 3,
        };
        modelFrame.Add(_modelDropdown);
        Add(modelFrame);

        // ── Status ──
        _statusLabel = new Label { X = 1, Y = 9, Width = 54, Text = "" };
        Add(_statusLabel);

        // ── Buttons ──
        // 获取模型: 用 Button + AddButton, Accepting 中获取后标记 handled 阻止关闭
        var fetchBtn = new Button { Text = "获取模型" };
        fetchBtn.Accepting += (_, e) =>
        {
            e.Handled = true; // 阻止 Dialog.OnAccepting 中的 RequestStop
            OnFetchModels(null, EventArgs.Empty);
        };
        AddButton(fetchBtn);

        // 保存: 关闭对话框
        var saveBtn = new Button { Text = "保存" };
        saveBtn.Accepted += OnSave;
        AddButton(saveBtn);

        var cancelBtn = new Button { Text = "取消" };
        cancelBtn.Accepted += (_, _) => _app.RequestStop();
        AddButton(cancelBtn);
    }

    public string? LayerSwitchRequested { get; private set; }

    private void OnProviderChanged(object? s, ValueChangedEventArgs<string?> e)
    {
        if (string.IsNullOrEmpty(e.NewValue)) return;
        var envVal = SecretManager.Get(Providers.FirstOrDefault(p => p.Service == e.NewValue)?.EnvVar);
        if (!string.IsNullOrEmpty(envVal)) _apiKeyField.Text = envVal;
    }

    private string SelectedProvider
    {
        get
        {
            var text = _providerDropdown.Text;
            var i = Array.FindIndex(Providers, p => p.Service.Equals(text, StringComparison.OrdinalIgnoreCase));
            return i >= 0 ? Providers[i].Service : text;
        }
    }

    private static (string provider, string model) LoadLayerConfig(string layer)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path)) return ("", "");
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
            var l = json?["LTAI"]?["AI"]?[layer];
            if (l == null) return ("", "");
            return (l["Provider"]?.GetValue<string>() ?? "", l["Model"]?.GetValue<string>() ?? "");
        }
        catch { return ("", ""); }
    }

    private void OnFetchModels(object? s, EventArgs e)
    {
        var provider = SelectedProvider;
        var apiKey = _apiKeyField.Text;
        if (string.IsNullOrEmpty(provider)) { _statusLabel.Text = "请选择 Provider"; return; }
        if (string.IsNullOrEmpty(apiKey)) { _statusLabel.Text = "请填写 API Key"; return; }

        var endpoint = Providers.FirstOrDefault(p => p.Service == provider)?.Endpoint;
        if (string.IsNullOrEmpty(endpoint)) { _statusLabel.Text = "未知 Provider"; return; }
        _statusLabel.Text = "正在获取...";

        Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                var url = endpoint.TrimEnd('/') switch
                {
                    string u when u.Contains("api.deepseek.com") => "https://api.deepseek.com/models",
                    string u when u.Contains("api.siliconflow.cn") => "https://api.siliconflow.cn/v1/models",
                    string u when u.Contains("openrouter.ai") => "https://openrouter.ai/api/v1/models",
                    _ => $"{endpoint.TrimEnd('/')}/models".Replace("//models", "/models"),
                };
                var resp = await http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) { _app.Invoke(() => _statusLabel.Text = $"HTTP {(int)resp.StatusCode}"); return; }
                var json = await resp.Content.ReadAsStringAsync();
                var doc = System.Text.Json.JsonDocument.Parse(json);
                var models = new List<string>();
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var m in data.EnumerateArray())
                        if (m.TryGetProperty("id", out var id)) models.Add(id.GetString() ?? "");
                _app.Invoke(() =>
                {
                    var mi = new ObservableCollection<string>(models);
                    _modelDropdown.Source = new ListWrapper<string>(mi);
                    if (models.Count > 0) { _modelDropdown.Text = models[0]; _statusLabel.Text = $"找到 {models.Count} 个模型"; }
                    else _statusLabel.Text = "未找到模型";
                });
            }
            catch (Exception ex) { _app.Invoke(() => _statusLabel.Text = $"失败: {ex.Message}"); }
        });
    }

    private void OnSave(object? s, EventArgs e)
    {
        var provider = SelectedProvider;
        var model = _modelDropdown.Text.Trim();

        if (string.IsNullOrEmpty(provider)) { _statusLabel.Text = "请选择 Provider"; return; }
        if (string.IsNullOrEmpty(model)) { _statusLabel.Text = "请选择模型"; return; }

        LTAIOptions.SaveLayerToAppSettings(_layer, provider, model);
        _statusLabel.Text = $"✅ {_layer} 已保存";
        _app.RequestStop();
    }

    public static void Run(IApplication app, string initialLayer)
    {
        var layer = initialLayer;
        while (layer != null)
        {
            var dlg = new ModelConfigDialog(app, layer);
            app.Run(dlg);
            layer = dlg.LayerSwitchRequested;
        }
    }
}
