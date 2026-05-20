using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LTAI.Core.Messaging;

public sealed class LivingEvent
{
    public string EventId { get; set; } = Guid.NewGuid().ToString();
    public string EventType { get; set; } = "";
    public string SourceOrgan { get; set; } = "system";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public Dictionary<string, object> Data { get; set; } = new();
    public int Priority { get; set; }
    public string? CorrelationId { get; set; }

    public static LivingEvent Create(string eventType, string sourceOrgan,
        Dictionary<string, object>? data = null, int priority = 0, string? correlationId = null)
    {
        return new LivingEvent
        {
            EventType = eventType,
            SourceOrgan = sourceOrgan,
            Data = data ?? new Dictionary<string, object>(),
            Priority = priority,
            CorrelationId = correlationId
        };
    }

    public Dictionary<string, object> ToDictionary()
    {
        return new Dictionary<string, object>
        {
            ["event_id"] = EventId,
            ["event_type"] = EventType,
            ["source_organ"] = SourceOrgan,
            ["timestamp"] = new DateTimeOffset(Timestamp).ToUnixTimeMilliseconds() / 1000.0,
            ["data"] = Data,
            ["priority"] = Priority,
            ["correlation_id"] = CorrelationId ?? ""
        };
    }
}

public sealed class EventFilter
{
    public string EventPattern { get; set; } = "*";
    public string? SourceOrgan { get; set; }
    public int PriorityMin { get; set; }
    public int PriorityMax { get; set; } = 3;
    public string? CorrelationId { get; set; }
    public Dictionary<string, object> DataFields { get; set; } = new();

    public bool Matches(LivingEvent evt)
    {
        if (EventPattern != "*" && !MatchesPattern(evt.EventType, EventPattern))
            return false;

        if (SourceOrgan != null && SourceOrgan != "*" && evt.SourceOrgan != SourceOrgan)
            return false;

        if (evt.Priority < PriorityMin || evt.Priority > PriorityMax)
            return false;

        if (CorrelationId != null && evt.CorrelationId != CorrelationId)
            return false;

        foreach (var (key, value) in DataFields)
        {
            if (!evt.Data.TryGetValue(key, out var evtValue) || !Equals(evtValue, value))
                return false;
        }

        return true;
    }

    private static bool MatchesPattern(string input, string pattern)
    {
        if (pattern == "*") return true;
        if (!pattern.Contains('*'))
            return input == pattern;
        return global::System.Text.RegularExpressions.Regex.IsMatch(input,
            "^" + global::System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$");
    }
}

public sealed class EventBusV2
{
    private static readonly Lazy<EventBusV2> _instance = new(() => new EventBusV2());
    public static EventBusV2 Instance => _instance.Value;

    private const int DefaultRingSize = 10000;
    private const int OrganRingSize = 5000;

    private readonly Dictionary<string, List<(EventFilter? filter, Action<LivingEvent> handler)>> _subscribers = new();
    private readonly LinkedList<LivingEvent> _eventHistory = new();
    private readonly Dictionary<string, LinkedList<LivingEvent>> _organHistory = new();
    private readonly object _lock = new();
    private int _publishedCount;
    private readonly int _ringSize;

    private EventBusV2(int ringSize = DefaultRingSize)
    {
        _ringSize = ringSize;
    }

    public string Publish(LivingEvent evt)
    {
        lock (_lock)
        {
            _eventHistory.AddLast(evt);
            if (_eventHistory.Count > _ringSize)
                _eventHistory.RemoveFirst();

            if (!_organHistory.ContainsKey(evt.SourceOrgan))
                _organHistory[evt.SourceOrgan] = new LinkedList<LivingEvent>();
            var organList = _organHistory[evt.SourceOrgan];
            organList.AddLast(evt);
            if (organList.Count > OrganRingSize)
                organList.RemoveFirst();

            _publishedCount++;
        }

        var candidates = new List<(EventFilter? filter, Action<LivingEvent> handler)>();
        lock (_lock)
        {
            if (_subscribers.TryGetValue(evt.EventType, out var subs))
                candidates.AddRange(subs);
            if (_subscribers.TryGetValue("*", out var wildSubs))
                candidates.AddRange(wildSubs);
        }

        foreach (var (filter, handler) in candidates)
        {
            if (filter != null && !filter.Matches(evt))
                continue;
            try
            {
                handler(evt);
            }
            catch (Exception ex)
            {
                global::System.Diagnostics.Debug.WriteLine($"EventBusV2 handler error: {ex.Message}");
            }
        }

        return evt.EventId;
    }

    public string Publish(string eventType, string sourceOrgan = "system",
        Dictionary<string, object>? data = null, int priority = 0, string? correlationId = null)
    {
        return Publish(LivingEvent.Create(eventType, sourceOrgan, data, priority, correlationId));
    }

    public void Subscribe(string eventType, Action<LivingEvent> handler, EventFilter? filter = null)
    {
        lock (_lock)
        {
            if (!_subscribers.ContainsKey(eventType))
                _subscribers[eventType] = new List<(EventFilter?, Action<LivingEvent>)>();
            _subscribers[eventType].Add((filter, handler));
        }
    }

    public bool Unsubscribe(string eventType, Action<LivingEvent> handler)
    {
        lock (_lock)
        {
            if (!_subscribers.TryGetValue(eventType, out var subs))
                return false;
            return subs.RemoveAll(s => s.handler == handler) > 0;
        }
    }

    public void SubscribeFiltered(EventFilter filter, Action<LivingEvent> handler)
    {
        Subscribe("*", handler, filter);
    }

    public void OrganSubscribe(string organName, string eventPattern, Action<LivingEvent> handler)
    {
        var filter = new EventFilter { EventPattern = eventPattern, SourceOrgan = organName };
        SubscribeFiltered(filter, handler);
    }

    public List<LivingEvent> GetOrganEvents(string organName, int limit = 100)
    {
        lock (_lock)
        {
            if (!_organHistory.TryGetValue(organName, out var list))
                return new List<LivingEvent>();
            return list.TakeLast(limit).ToList();
        }
    }

    public List<LivingEvent> CorrelationTrace(string correlationId)
    {
        List<LivingEvent> trace;
        lock (_lock)
        {
            trace = _eventHistory.Where(e => e.CorrelationId == correlationId)
                .OrderBy(e => e.Timestamp).ToList();
        }
        return trace;
    }

    public string StartCorrelation(string? correlationId = null)
    {
        correlationId ??= Guid.NewGuid().ToString();
        Publish(LivingEvent.Create("system.lifecycle", "system",
            new Dictionary<string, object> { ["phase"] = "correlation_start" },
            correlationId: correlationId));
        return correlationId;
    }

    public void EndCorrelation(string correlationId)
    {
        Publish(LivingEvent.Create("system.lifecycle", "system",
            new Dictionary<string, object> { ["phase"] = "correlation_end" },
            correlationId: correlationId));
    }

    public List<LivingEvent> GetEventHistory(int limit = 100)
    {
        lock (_lock)
        {
            return _eventHistory.TakeLast(limit).ToList();
        }
    }

    public int PublishedCount => _publishedCount;
    public int RingSize => _ringSize;

    public int GetSubscriberCount()
    {
        lock (_lock)
        {
            return _subscribers.Values.Sum(v => v.Count);
        }
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            _eventHistory.Clear();
            _organHistory.Clear();
        }
    }

    public void Emit(string eventType, Dictionary<string, object>? data = null)
    {
        Publish(eventType, "system", data);
    }
}
