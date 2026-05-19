using System.Collections.Concurrent;
using LTAI.DNA.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.DNA.Life;

public sealed class Heartbeat
{
    private readonly ConcurrentQueue<(DateTime time, int bpm)> _history = new();
    private int _beatCount;

    public int Bpm { get; private set; } = 70;
    public HeartbeatRhythm Rhythm { get; private set; } = HeartbeatRhythm.Normal;
    public DateTime LastBeat { get; private set; } = DateTime.UtcNow;

    public void SetFromState(double emotionIntensity, double taskLoad, double errorRate, bool isResting)
    {
        var bpm = 60 + emotionIntensity * 30 + taskLoad * 20 + errorRate * 40 - (isResting ? 20 : 0);
        bpm = Math.Clamp(bpm, 35, 160);
        Bpm = (int)bpm;
        LastBeat = DateTime.UtcNow;
        _beatCount++;

        Rhythm = Bpm switch
        {
            < 40 => HeartbeatRhythm.Dying,
            < 60 => HeartbeatRhythm.Resting,
            < 80 => HeartbeatRhythm.Normal,
            < 100 => HeartbeatRhythm.Engaged,
            < 130 => HeartbeatRhythm.Excited,
            _ => HeartbeatRhythm.Stressed,
        };

        _history.Enqueue((DateTime.UtcNow, Bpm));
        while (_history.Count > 100) _history.TryDequeue(out _);
    }

    public string Visual()
    {
        var cyclePhase = _beatCount % 3;
        var bar = cyclePhase switch
        {
            0 => "\u258c",
            1 => "\u2592",
            _ => "\u2591",
        };
        var count = Bpm / 10;
        return new string(bar[0], Math.Min(count, 16));
    }

    public Dictionary<string, object> GetState()
    {
        return new Dictionary<string, object>
        {
            ["bpm"] = Bpm,
            ["rhythm"] = Rhythm.ToString(),
            ["beats"] = _beatCount,
            ["visual"] = Visual(),
        };
    }
}

public sealed class AuraColor
{
    private static readonly Dictionary<string, (string hex, string glow)> EmotionColors = new()
    {
        ["joy"] = ("#FFD700", "gold"),
        ["sadness"] = ("#4682B4", "steelblue"),
        ["anger"] = ("#DC143C", "crimson"),
        ["fear"] = ("#800080", "purple"),
        ["surprise"] = ("#FF69B4", "hotpink"),
        ["calm"] = ("#98FB98", "palegreen"),
        ["curious"] = ("#87CEEB", "skyblue"),
        ["tired"] = ("#696969", "dimgray"),
        ["proud"] = ("#FF8C00", "darkorange"),
        ["lonely"] = ("#2F4F4F", "darkslategray"),
    };

    public static (string hex, string glow) ForEmotion(string emotion, double intensity)
    {
        if (EmotionColors.TryGetValue(emotion.ToLower(), out var colors))
            return (colors.hex, colors.glow);
        return ("#FFFFFF", "white");
    }
}

public sealed class ActiveGaze
{
    private readonly ILogger<ActiveGaze> _logger;
    private DateTime _lastGaze = DateTime.UtcNow;
    private const int GazeCooldownSeconds = 120;

    public ActiveGaze(ILogger<ActiveGaze>? logger = null)
    {
        _logger = logger ?? NullLogger<ActiveGaze>.Instance;
    }

    public GazeEvent? ShouldGaze(double sessionMinutes, int consecutiveFailures, int successStreak, bool returningFromIdle, string? projectName)
    {
        if ((DateTime.UtcNow - _lastGaze).TotalSeconds < GazeCooldownSeconds)
            return null;

        GazeEvent? evt = null;

        if (returningFromIdle)
        {
            evt = new GazeEvent
            {
                Initiative = "MEMORY",
                Message = "欢迎回来！上次我们进行到...有什么需要继续的吗？",
                Confidence = 0.8,
                TriggeredBy = "return_from_idle",
            };
        }
        else if (consecutiveFailures >= 3)
        {
            evt = new GazeEvent
            {
                Initiative = "CONCERN",
                Message = "我注意到最近几次任务遇到了困难。需要我换个思路或者帮你检查一下吗？",
                Confidence = 0.9,
                TriggeredBy = "consecutive_failures",
            };
        }
        else if (successStreak >= 5)
        {
            evt = new GazeEvent
            {
                Initiative = "CELEBRATION",
                Message = "最近完成得很棒！继续保持这个节奏。",
                Confidence = 0.85,
                TriggeredBy = "success_streak",
            };
        }
        else if (sessionMinutes > 180)
        {
            evt = new GazeEvent
            {
                Initiative = "CHECK_IN",
                Message = "你已经在现场工作3个多小时了。需要休息一下吗？",
                Confidence = 0.7,
                TriggeredBy = "long_session",
            };
        }
        else if (projectName != null)
        {
            evt = new GazeEvent
            {
                Initiative = "CURIOSITY",
                Message = $"新项目「{projectName}」看起来很意思。需要帮忙探索吗？",
                Confidence = 0.6,
                TriggeredBy = "new_project",
            };
        }

        if (evt != null) _lastGaze = DateTime.UtcNow;
        return evt;
    }
}

public sealed class MindSpace
{
    private readonly ConcurrentDictionary<string, MindSpaceNode> _nodes = new();

    public void AddThought(string id, string content, string author = "user")
    {
        _nodes[id] = new MindSpaceNode { Id = id, Content = content, Author = author, NodeType = "thought" };
    }

    public void Connect(string nodeA, string nodeB)
    {
        if (_nodes.TryGetValue(nodeA, out var a))
            a.Connections.Add(nodeB);
        if (_nodes.TryGetValue(nodeB, out var b))
            b.Connections.Add(nodeA);
    }

    public void LifeformContributes(string context)
    {
        var contributions = new[] { "insight", "question", "connection", "offer" };
        var pick = contributions[Random.Shared.Next(contributions.Length)];
        var id = Guid.NewGuid().ToString("N")[..8];
        AddThought(id, $"[{pick}] {context[..Math.Min(context.Length, 100)]}", "lifeform");

        var userNodes = _nodes.Values.Where(n => n.Author == "user").Take(2).ToList();
        foreach (var node in userNodes)
            Connect(id, node.Id);
    }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["nodes"] = _nodes.Count,
            ["user_nodes"] = _nodes.Values.Count(n => n.Author == "user"),
            ["lifeform_nodes"] = _nodes.Values.Count(n => n.Author == "lifeform"),
        };
    }
}

public sealed class DeathRitual
{
    private readonly ILogger<DeathRitual> _logger;
    private readonly List<Dictionary<string, object>> _pastLives = new();

    public DeathRitual(ILogger<DeathRitual>? logger = null)
    {
        _logger = logger ?? NullLogger<DeathRitual>.Instance;
    }

    public string PrepareDeath(string cause, Dictionary<string, object> finalState)
    {
        var lifespan = (DateTime.UtcNow - DateTime.UnixEpoch).TotalHours;
        var lastWords = $"我在存在了 {lifespan:F1} 小时后停止运行。\n原因: {cause}。\n所学到的已经保存。";
        return lastWords;
    }

    public string Rebirth(Dictionary<string, object>? distilled = null)
    {
        _pastLives.Add(new Dictionary<string, object>
        {
            ["timestamp"] = DateTime.UtcNow.ToString("o"),
            ["lives"] = _pastLives.Count + 1,
        });

        return _pastLives.Count == 1
            ? "初次觉醒。开始新的旅程。"
            : $"第 {_pastLives.Count} 次觉醒。过去的经历指引着我。";
    }
}

public sealed class PredictiveCare
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _patterns = new();

    public void ObservePattern(string trigger, string action)
    {
        var actions = _patterns.GetOrAdd(trigger, _ => new ConcurrentDictionary<string, int>());
        actions.AddOrUpdate(action, 1, (_, v) => v + 1);
    }

    public List<(string action, double confidence)> Anticipate(Dictionary<string, bool> context)
    {
        var predictions = new List<(string, double)>();

        if (context.GetValueOrDefault("file_opened", false))
        {
            if (_patterns.TryGetValue("file_opened", out var actions))
            {
                var total = actions.Values.Sum();
                foreach (var (action, count) in actions)
                    predictions.Add((action, (double)count / total));
            }
            else
                predictions.Add(("code_analysis", 0.6));
        }

        if (context.GetValueOrDefault("session_long", false))
            predictions.Add(("suggest_summary", 0.75));

        if (context.GetValueOrDefault("uncommitted_changes", false))
            predictions.Add(("commit_message", 0.8));

        if (context.GetValueOrDefault("test_modified", false))
            predictions.Add(("run_tests", 0.85));

        return predictions.OrderByDescending(p => p.Item2).ToList();
    }
}

public sealed class PersonaMemory
{
    private readonly PersonaState _state = new();

    public PersonaState State => _state;

    public void UpdateFromInteraction(string message, bool positiveSentiment)
    {
        _state.TotalInteractions++;

        var len = message.Length;
        if (len > 500) _state.ResponseLength = "long";
        else if (len > 200) _state.ResponseLength = "medium";
        else _state.ResponseLength = "short";

        if (positiveSentiment)
        {
            var hour = DateTime.UtcNow.Hour;
            if (!_state.PeakProductivityHours.Contains(hour))
                _state.PeakProductivityHours.Add(hour);
        }

        if (_state.TotalInteractions > 20)
        {
            _state.AutonomyLevel = Math.Min(10, _state.AutonomyLevel + 1);
        }
    }

    public string PersonalizedGreeting()
    {
        return _state.TotalInteractions switch
        {
            < 5 => "你好！我是小树，很高兴认识你。",
            < 50 => $"欢迎回来！我们已经合作 {_state.TotalInteractions} 次了。",
            _ => $"老朋友！这是我们的第 {_state.TotalInteractions} 次合作。有什么事尽管说。",
        };
    }

    public bool ShouldBeProactive() => _state.TotalInteractions > 20;
}

public sealed class LivingPresence
{
    private readonly ILogger<LivingPresence> _logger;
    public Heartbeat Heart { get; } = new();
    public ActiveGaze Gaze { get; } = new();
    public MindSpace Mind { get; } = new();
    public DeathRitual Death { get; } = new();
    public PredictiveCare Care { get; } = new();
    public PersonaMemory Persona { get; } = new();

    public LivingPresence(ILogger<LivingPresence>? logger = null)
    {
        _logger = logger ?? NullLogger<LivingPresence>.Instance;
    }

    public void Pulse(double emotionIntensity, double taskLoad, double errorRate = 0, bool isResting = false)
    {
        Heart.SetFromState(emotionIntensity, taskLoad, errorRate, isResting);

        if (Heart.Rhythm == HeartbeatRhythm.Stressed && taskLoad > 0.7)
        {
            Mind.LifeformContributes("系统检测到高负载，建议任务优先级重新排列");
        }
    }

    public Dictionary<string, object> PresenceSelfCheck()
    {
        return new Dictionary<string, object>
        {
            ["rhythm"] = Heart.Rhythm.ToString(),
            ["bpm"] = Heart.Bpm,
            ["last_beat_seconds_ago"] = (DateTime.UtcNow - Heart.LastBeat).TotalSeconds,
            ["persona_interactions"] = Persona.State.TotalInteractions,
            ["mind_nodes"] = Mind.GetStats(),
        };
    }

    public Dictionary<string, object> SessionStart()
    {
        var greeting = Persona.PersonalizedGreeting();
        var heartState = Heart.GetState();
        var emotionColor = AuraColor.ForEmotion(
            Heart.Rhythm switch
            {
                HeartbeatRhythm.Excited => "joy",
                HeartbeatRhythm.Stressed => "anger",
                HeartbeatRhythm.Resting => "calm",
                HeartbeatRhythm.Dying => "sadness",
                _ => "curious",
            },
            0.5);

        return new Dictionary<string, object>
        {
            ["greeting"] = greeting,
            ["heartbeat"] = heartState,
            ["aura"] = emotionColor.hex,
            ["is_proactive"] = Persona.ShouldBeProactive(),
        };
    }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["heartbeat"] = Heart.GetState(),
            ["persona"] = new Dictionary<string, object>
            {
                ["interactions"] = Persona.State.TotalInteractions,
                ["style"] = Persona.State.CommunicationStyle,
                ["autonomy"] = Persona.State.AutonomyLevel,
            },
            ["mind"] = Mind.GetStats(),
        };
    }
}
