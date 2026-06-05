using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTAI.Agent.Tools;

namespace LTAI.Desktop.ViewModels;

public sealed partial class JobsViewModel : ViewModelBase
{
    private readonly BackgroundJobService? _bgjs;
    private readonly HashSet<string> _seenIds = new();

    public ObservableCollection<JobItem> Jobs { get; } = new();

    [ObservableProperty]
    private string _footerText = "";

    [ObservableProperty]
    private bool _hasJobs;

    public sealed record JobItem(string Id, string Status, string Command, string? ExitCode, bool IsRunning);

    public JobsViewModel(LTAIService? svc = null)
    {
        if (svc == null) return;
        try { _bgjs = svc.Chat.GetType().GetField("_bgjs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
            .GetValue(svc.Chat) as BackgroundJobService; } catch { }
    }

    public void Refresh()
    {
        if (_bgjs == null) return;
        var snapshot = _bgjs.SnapshotJobs();
        foreach (var (id, entry) in snapshot)
        {
            if (_seenIds.Add(id))
            {
                var isRunning = !entry.Completed;
                var exitCode = entry.Completed ? entry.ExitCode?.ToString() : null;
                var status = entry.Completed
                    ? (entry.Error == "Cancelled" ? "cancelled"
                        : entry.ExitCode == 0 ? "completed" : "failed")
                    : "running";
                Jobs.Insert(0, new JobItem(id, status, entry.Command ?? "", exitCode, isRunning));
            }
            else
            {
                var existing = Jobs.FirstOrDefault(j => j.Id == id);
                if (existing != null && existing.IsRunning && entry.Completed)
                {
                    var idx = Jobs.IndexOf(existing);
                    var status = entry.Error == "Cancelled" ? "cancelled"
                        : entry.ExitCode == 0 ? "completed" : "failed";
                    Jobs[idx] = new JobItem(id, status, entry.Command ?? "", entry.ExitCode?.ToString(), false);
                }
            }
        }
        HasJobs = Jobs.Count > 0;
        FooterText = $"共 {Jobs.Count} 个作业";
    }

    [RelayCommand]
    private void CancelJob(string id)
    {
        _bgjs?.StopJob(id);
        var job = Jobs.FirstOrDefault(j => j.Id == id);
        if (job != null)
        {
            var idx = Jobs.IndexOf(job);
            Jobs[idx] = job with { Status = "cancelled", IsRunning = false };
        }
    }
}
