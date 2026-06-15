using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTAI.Core.Configuration;

namespace LTAI.Desktop.ViewModels;

public sealed partial class ConfigViewModel : ViewModelBase
{
    public ObservableCollection<KeyStatus> Keys { get; } = new();

    [ObservableProperty]
    private string _statusBarText = "";

    [ObservableProperty]
    private bool _isLoadingModels;

    [ObservableProperty]
    private ObservableCollection<string> _l1Models = new();

    [ObservableProperty]
    private ObservableCollection<string> _l2Models = new();

    [ObservableProperty]
    private string? _selectedL1Model;

    [ObservableProperty]
    private string? _selectedL2Model;

    public sealed record KeyStatus(string EnvVar, string Service, bool IsSet);

    public ConfigViewModel()
    {
        RefreshKeys();
    }

    public void RefreshKeys()
    {
        Keys.Clear();
        foreach (var k in KnownKeys.All)
            Keys.Add(new KeyStatus(k.EnvVar, k.Service, SecretManager.Has(k.EnvVar)));
    }

    [RelayCommand]
    private async Task FetchModelsAsync()
    {
        StatusBarText = "正在获取模型列表...";
        IsLoadingModels = true;
        HttpClient? ownedHttp = null;
        try
        {
            var key = KnownKeys.All.Select(k => SecretManager.Get(k.EnvVar))
                .FirstOrDefault(k => !string.IsNullOrEmpty(k));
            if (string.IsNullOrEmpty(key))
            { StatusBarText = "⚠️ 未配置 API Key"; return; }

            var keyInfo = KnownKeys.All.FirstOrDefault(k => SecretManager.Has(k.EnvVar));
            var endpoint = keyInfo?.Endpoint ?? "https://api.deepseek.com/v1";

            var http = App.HttpFactory?.CreateClient() ?? (ownedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
            var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/models");
            req.Headers.Authorization = new("Bearer", key);
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            { StatusBarText = $"⚠️ API 返回 {(int)resp.StatusCode}"; return; }

            using var json = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
            var models = json.RootElement.GetProperty("data")
                .EnumerateArray()
                .Select(m => m.GetProperty("id").GetString() ?? "")
                .Where(id => !string.IsNullOrEmpty(id))
                .OrderBy(id => id)
                .ToList();

            L1Models = new ObservableCollection<string>(models);
            L2Models = new ObservableCollection<string>(models);
            if (models.Count > 0) SelectedL1Model = models[0];
            if (models.Count > 1) SelectedL2Model = models[1];
            StatusBarText = $"✅ 获取到 {models.Count} 个模型";
        }
        catch (Exception ex) { StatusBarText = $"❌ 失败: {ex.Message}"; }
        finally { IsLoadingModels = false; ownedHttp?.Dispose(); }
    }

    [RelayCommand]
    private void ClearAllKeys()
    {
        var known = KnownKeys.All.Select(k => k.EnvVar).Distinct()
            .Where(e => !string.IsNullOrEmpty(e) && SecretManager.Has(e)).ToList();
        if (known.Count == 0) { StatusBarText = "当前没有任何已设置的 Key"; return; }
        foreach (var envVar in known)
        {
            SecretManager.Set(envVar, null, persistent: true);
            SecretManager.Invalidate(envVar);
        }
        RefreshKeys();
        StatusBarText = $"✅ 已清除 {known.Count} 个 Key";
    }
}
