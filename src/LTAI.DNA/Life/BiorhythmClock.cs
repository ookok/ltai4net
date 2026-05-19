using LTAI.DNA.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.DNA.Life;

public sealed class BiorhythmEngine
{
    private readonly ILogger<BiorhythmEngine> _logger;
    private readonly BiorhythmState _state = new();
    private DateTime _bornAt = DateTime.UtcNow;
    private DateTime _lastActive = DateTime.UtcNow;
    private int _dreamCount;
    public IReadOnlyDictionary<string, double> Metrics => new Dictionary<string, double>
    {
        ["heart_rate"] = _state.EnergyLevel * 70 + 30,
        ["respiration"] = ComputeRespiration(),
        ["activity_level"] = _state.EnergyLevel,
        ["cycle_count"] = _state.CycleProgress * 100,
    };

    public BiorhythmEngine(ILogger<BiorhythmEngine>? logger = null)
    {
        _logger = logger ?? NullLogger<BiorhythmEngine>.Instance;
    }

    public void Pulse()
    {
        _state.EnergyLevel = Math.Min(1.0, _state.EnergyLevel + 0.3);
        _lastActive = DateTime.UtcNow;
    }

    public BiorhythmState Tick()
    {
        var now = DateTime.UtcNow;
        var idle = (now - _lastActive).TotalSeconds;
        var uptime = (now - _bornAt).TotalSeconds;

        _state.EnergyLevel *= 0.95;

        if (idle < 30)
            _state.Phase = BiorhythmPhase.Peak;
        else if (idle < 120)
            _state.Phase = BiorhythmPhase.Plateau;
        else if (idle < 600)
            _state.Phase = BiorhythmPhase.Decline;
        else
        {
            _state.Phase = BiorhythmPhase.Trough;
            _dreamCount++;
        }

        if (_dreamCount >= 30)
        {
            _state.Phase = BiorhythmPhase.Recovery;
            _dreamCount = 0;
        }

        _state.FocusLevel = Math.Clamp(_state.EnergyLevel * 0.9 + Math.Sin(uptime * 0.1) * 0.1, 0, 1);
        _state.CreativityLevel = Math.Clamp(_state.EnergyLevel * 0.7 + Math.Cos(uptime * 0.05) * 0.2, 0, 1);
        _state.SocialDrive = Math.Clamp(1.0 - (_state.FocusLevel * 0.5), 0, 1);
        _state.CycleProgress = (uptime % 3600) / 3600.0;

        return _state;
    }

    private double ComputeRespiration()
    {
        var t = (DateTime.UtcNow - _bornAt).TotalSeconds;
        var freq = _state.Phase switch
        {
            BiorhythmPhase.Peak => 0.3,
            BiorhythmPhase.Decline => 0.1,
            BiorhythmPhase.Trough => 0.05,
            _ => 0.15,
        };
        return 0.5 + 0.5 * Math.Sin(t * freq * 2 * Math.PI);
    }

    public Dictionary<string, object> GetSnapshot()
    {
        return new Dictionary<string, object>
        {
            ["phase"] = _state.Phase.ToString(),
            ["energy"] = Math.Round(_state.EnergyLevel, 3),
            ["focus"] = Math.Round(_state.FocusLevel, 3),
            ["creativity"] = Math.Round(_state.CreativityLevel, 3),
            ["social_drive"] = Math.Round(_state.SocialDrive, 3),
            ["heart_rate"] = Math.Round(_state.EnergyLevel * 70 + 30, 1),
            ["respiration"] = Math.Round(ComputeRespiration(), 3),
            ["dreams"] = _dreamCount,
            ["uptime_h"] = Math.Round((DateTime.UtcNow - _bornAt).TotalHours, 2),
        };
    }
}
