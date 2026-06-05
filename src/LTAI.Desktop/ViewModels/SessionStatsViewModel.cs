using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LTAI.Core.Session;

namespace LTAI.Desktop.ViewModels;

public sealed partial class SessionStatsViewModel : ViewModelBase
{
    private readonly SessionManager _sessions;

    public ObservableCollection<SessionGroup> Groups { get; } = new();

    public event Action<string?>? SessionSelected;
    public event Action? NewSessionClicked;

    public sealed record SessionGroup(string Title, ObservableCollection<SessionItem> Items);
    public sealed record SessionItem(string Name, string Preview, string Time, bool IsCurrent);

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private int _totalSessions;

    public SessionStatsViewModel(SessionManager sessions)
    {
        _sessions = sessions;
    }

    public void Refresh()
    {
        Groups.Clear();
        var all = _sessions.ListSessions();
        TotalSessions = all.Length;

        var groups = all
            .GroupBy(s => GetGroupKey(s.Name))
            .OrderBy(g => Array.IndexOf(_groupOrder, g.Key) >= 0 ? Array.IndexOf(_groupOrder, g.Key) : int.MaxValue);

        foreach (var g in groups)
        {
            var items = new ObservableCollection<SessionItem>();
            foreach (var s in g.OrderByDescending(x => x.Name))
            {
                var preview = s.DisplayName.Length > 60 ? s.DisplayName[..60] + "..." : s.DisplayName;
                var time = s.Name.Length >= 12 ? s.Name[..12] : s.Name;
                var isCurrent = s.Name == _sessions.CurrentHandle?.Name;
                items.Add(new SessionItem(s.Name, preview, time, isCurrent));
            }
            Groups.Add(new SessionGroup(g.Key, items));
        }
    }

    public void SelectSession(string? name) => SessionSelected?.Invoke(name);
    public void NewSession() => NewSessionClicked?.Invoke();

    private static string GetGroupKey(string sessionName)
    {
        if (sessionName.Length < 8) return "今天";
        if (DateTime.TryParse(sessionName[..10], out var dt))
        {
            var now = DateTime.Now;
            if (dt.Date == now.Date) return "今天";
            if (dt.Date == now.Date.AddDays(-1)) return "昨天";
            if (dt > now.AddDays(-7)) return "本周";
            if (dt > now.AddMonths(-1)) return "本月";
        }
        return "更早";
    }

    private static readonly string[] _groupOrder = ["今天", "昨天", "本周", "本月", "更早"];
}
