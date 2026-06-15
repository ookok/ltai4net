using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTAI.Agent.Workflows;

namespace LTAI.Desktop.ViewModels;

public sealed partial class WorkflowsViewModel : ViewModelBase
{
    private readonly YAMLWorkflowRegistry? _registry;

    public ObservableCollection<WorkflowItem> Workflows { get; } = new();

    [ObservableProperty]
    private string _statusText = "加载中...";

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string _lastReloadText = "";

    public sealed record WorkflowItem(string Name, string Type, string Version);

    public WorkflowsViewModel(LTAIService? svc = null, YAMLWorkflowRegistry? registry = null)
    {
        _registry = registry ?? svc?.Chat.GetType().GetField("_workflowRegistry",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                .GetValue(svc.Chat) as YAMLWorkflowRegistry;

        if (_registry != null)
        {
            RefreshList();
            StatusText = $"共 {Workflows.Count} 个工作流";
            LastReloadText = $"监控目录: {_registry.WatchDirectory}";
        }
        else
            StatusText = "工作流注册表不可用";
    }

    public void RefreshList()
    {
        Workflows.Clear();
        if (_registry == null) return;
        foreach (var wf in _registry.List())
            Workflows.Add(new WorkflowItem(wf.Name, wf.Type, wf.Version.ToString()));
    }

    [RelayCommand]
    private async Task ReloadAllAsync()
    {
        if (_registry == null) return;
        StatusText = "重新加载中...";
        try
        {
            await _registry.ReloadAllAsync();
            RefreshList();
            StatusText = $"✅ 已重新加载 {Workflows.Count} 个工作流";
        }
        catch (Exception ex)
        {
            ErrorText = $"加载失败: {ex.Message}";
            StatusText = "❌ 加载失败";
        }
    }
}
