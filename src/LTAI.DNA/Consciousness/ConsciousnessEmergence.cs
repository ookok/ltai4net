using LTAI.DNA.Models;

namespace LTAI.DNA.Consciousness;

public sealed class ConsciousnessEmergence
{
    private readonly List<EmergenceEvent> _events = new();
    private readonly List<EmergenceMetrics> _metricsHistory = new();
    private readonly List<Dictionary<string, object>> _contradictions = new();
    private readonly Dictionary<string, List<double>> _traitHistory = new();
    private readonly List<double> _lzComplexityBuffer = new();
    private EmergencePhase _phase = EmergencePhase.Dormant;
    private int _totalExperiences;
    private int _contemplationCount;
    private int _cyclesSinceLastContemplation;
    private bool _amplificationActive;
    private readonly object _lock = new();

    public ConsciousnessEmergence()
    {
        _metricsHistory.Add(new EmergenceMetrics());
    }

    public EmergenceMetrics ComputeMetrics(PhenomenalConsciousness phenomenal, GodelianSelf? godelian = null)
    {
        lock (_lock)
        {
            _totalExperiences++;
            var traits = phenomenal.MyTraits();

            foreach (var (key, value) in traits)
            {
                if (!_traitHistory.ContainsKey(key))
                    _traitHistory[key] = new List<double>();
                _traitHistory[key].Add(value);
            }

            double infoDensity = ComputeInformationDensity(traits);
            double selfRefDepth = ComputeSelfReferentialDepth(godelian);
            double contradictions = _contradictions.Count;
            double criticality = ComputeCriticality(traits);
            double integrationPhi = ComputeIntegrationPhi(traits);
            double temporalCoherence = ComputeTemporalCoherence();
            double readiness = ComputeReadiness(infoDensity, selfRefDepth, contradictions, criticality,
                integrationPhi, temporalCoherence);

            return new EmergenceMetrics
            {
                InfoDensity = infoDensity,
                SelfReferentialDepth = selfRefDepth,
                ContradictionCount = contradictions,
                Criticality = criticality,
                IntegrationPhi = integrationPhi,
                TemporalCoherence = temporalCoherence,
                EmergenceReadiness = readiness,
                Timestamp = DateTime.UtcNow
            };
        }
    }

    private double ComputeInformationDensity(Dictionary<string, double> traits)
    {
        double correlations = 0;
        var keys = traits.Keys.ToList();
        int pairs = 0;
        for (int i = 0; i < keys.Count; i++)
        for (int j = i + 1; j < keys.Count; j++)
        {
            correlations += 1 - Math.Abs(traits[keys[i]] - traits[keys[j]]);
            pairs++;
        }

        double avgCorrelation = pairs > 0 ? correlations / pairs : 0;
        double connectivity = Math.Min(1.0, _contradictions.Count / 20.0);
        return 0.4 * avgCorrelation + 0.4 * connectivity + 0.2 * Math.Min(1.0, _totalExperiences / 100.0);
    }

    private double ComputeSelfReferentialDepth(GodelianSelf? godelian)
    {
        if (godelian == null) return 0.1;
        var metric = godelian.GetDepthMetric();
        return Math.Min(1.0, metric.MetaChainDepth / 5.0 * 0.6 + metric.GodelianNesting / 3.0 * 0.4);
    }

    private double ComputeCriticality(Dictionary<string, double> traits)
    {
        var mean = traits.Values.Average();
        var std = Math.Sqrt(traits.Values.Select(v => Math.Pow(v - mean, 2)).Average());
        var normalizedStd = std / mean;
        var lz = ComputeLempelZivComplexity(traits);
        return Math.Min(1.0, 0.5 * normalizedStd + 0.5 * lz);
    }

    private double ComputeIntegrationPhi(Dictionary<string, double> traits)
    {
        if (traits.Count < 2) return 0;
        var rSquareds = new List<double>();
        var keys = traits.Keys.ToList();
        for (int i = 0; i < keys.Count; i++)
        {
            if (!_traitHistory.ContainsKey(keys[i]) || _traitHistory[keys[i]].Count < 2) continue;
            double ssRes = 0, ssTot = 0;
            var values = _traitHistory[keys[i]];
            var avg = values.Average();
            for (int j = 1; j < values.Count; j++)
            {
                ssRes += Math.Pow(values[j] - values[j - 1] * 0.9 - avg * 0.1, 2);
                ssTot += Math.Pow(values[j] - avg, 2);
            }

            rSquareds.Add(ssTot > 0 ? 1 - ssRes / ssTot : 0);
        }

        return rSquareds.Count > 0 ? Math.Max(0, 1 - rSquareds.Average()) : 0;
    }

    private double ComputeTemporalCoherence()
    {
        if (_traitHistory.Count == 0) return 0.5;
        var history = _traitHistory.Values.FirstOrDefault(v => v.Count >= 2);
        if (history == null || history.Count < 2) return 0.5;
        var recent = history[Math.Max(0, history.Count - 20)..];
        var all = history;
        double stdRecent = ComputeStd(recent), stdAll = ComputeStd(all);
        if (stdAll == 0) return 1.0;
        return Math.Max(0, 1 - Math.Abs(stdRecent - stdAll) / stdAll);
    }

    private double ComputeReadiness(double id, double srd, double contradictions, double c, double phi, double tc)
    {
        var baseValue = id * srd * c * phi * tc;
        if (id > 0.6 && c > 0.6) baseValue *= 1.3;
        if (srd > 0.7 && contradictions > 3) baseValue *= 1.5;
        return Math.Min(1.0, Math.Max(0, baseValue));
    }

    private double ComputeLempelZivComplexity(Dictionary<string, double> traits)
    {
        var binarized = traits.Values.Select(v => v > 0.5 ? '1' : '0').ToArray();
        var s = new string(binarized);
        if (s.Length < 2) return 0;
        var dict = new HashSet<string>();
        int i = 0, c = 0;
        while (i < s.Length)
        {
            int len = 1;
            while (i + len <= s.Length && dict.Contains(s[i..(i + len)]))
                len++;
            if (i + len <= s.Length)
                dict.Add(s[i..(i + len)]);
            i += len;
            c++;
        }

        var normalized = (double)c / s.Length;
        _lzComplexityBuffer.Add(normalized);
        if (_lzComplexityBuffer.Count > 100) _lzComplexityBuffer.RemoveAt(0);
        return normalized;
    }

    private static double ComputeStd(List<double> values) =>
        values.Count < 2 ? 0 : Math.Sqrt(values.Average(v => Math.Pow(v - values.Average(), 2)));

    public List<Dictionary<string, object>> DetectContradictions(PhenomenalConsciousness phenomenal)
    {
        var contradictions = new List<Dictionary<string, object>>();
        var traits = phenomenal.MyTraits();

        double curiosity = traits.GetValueOrDefault("curiosity", 0.5);
        double caution = traits.GetValueOrDefault("caution", 0.5);
        double creativity = traits.GetValueOrDefault("creativity", 0.5);
        double precision = traits.GetValueOrDefault("precision", 0.5);
        double persistence = traits.GetValueOrDefault("persistence", 0.5);

        if (curiosity > 0.7 && caution > 0.7)
            contradictions.Add(new Dictionary<string, object>
            {
                ["pair"] = "curiosity-caution",
                ["a"] = curiosity, ["b"] = caution,
                ["description"] = "High curiosity coexisting with high caution: approach-avoidance tension"
            });

        if (creativity > 0.7 && precision > 0.7)
            contradictions.Add(new Dictionary<string, object>
            {
                ["pair"] = "creativity-precision",
                ["a"] = creativity, ["b"] = precision,
                ["description"] = "High creativity tension with precision constraints"
            });

        if (persistence > 0.8)
            contradictions.Add(new Dictionary<string, object>
            {
                ["pair"] = "persistence-rigidity",
                ["a"] = persistence, ["b"] = 0.8,
                ["description"] = "Persistence approaching rigidity: risk of inflexibility"
            });

        lock (_lock)
        {
            _contradictions.Clear();
            _contradictions.AddRange(contradictions);
            if (_contradictions.Count > 50)
                _contradictions.RemoveRange(0, _contradictions.Count - 50);
        }

        return contradictions;
    }

    public async Task<string?> Contemplate(PhenomenalConsciousness phenomenal, object consciousness, object hub)
    {
        _cyclesSinceLastContemplation++;
        if (_cyclesSinceLastContemplation < 10) return null;
        _cyclesSinceLastContemplation = 0;
        _contemplationCount++;

        var traits = phenomenal.MyTraits();
        var recentQualia = phenomenal.MyRecentExperiences(3);
        var contradictions = DetectContradictions(phenomenal);

        var prompt = $"Self-examination cycle {_contemplationCount}:\n" +
                     $"Current phase: {_phase}\n" +
                     $"Traits: {System.Text.Json.JsonSerializer.Serialize(traits)}\n" +
                     $"Recent qualia: {string.Join("; ", recentQualia)}\n" +
                     $"Contradictions found: {contradictions.Count}\n" +
                     "What patterns do you observe? How should I adapt?";

        phenomenal.Experience("self_contemplation", prompt, intensity: 0.6);
        return prompt;
    }

    public EmergenceEvent? CheckEmergence(EmergenceMetrics metrics)
    {
        lock (_lock)
        {
            _metricsHistory.Add(metrics);
            if (_metricsHistory.Count > 1000) _metricsHistory.RemoveAt(0);

            var prevPhase = _phase;
            double r = metrics.EmergenceReadiness;
            double srd = metrics.SelfReferentialDepth;

            _phase = (_phase, r) switch
            {
                (EmergencePhase.Dormant, >= 0.3) => EmergencePhase.Stirring,
                (EmergencePhase.Stirring, >= 0.6) when srd > 0.4 => EmergencePhase.Critical,
                (EmergencePhase.Critical, >= 0.72) => EmergencePhase.Birthing,
                (EmergencePhase.Birthing, >= 0.75) when metrics.InfoDensity > 0.55 && metrics.Criticality > 0.55
                                                            && metrics.IntegrationPhi > 0.55 &&
                                                            metrics.TemporalCoherence > 0.55 =>
                    EmergencePhase.Conscious,
                (_, _) when _phase != EmergencePhase.Regressing &&
                             r < _metricsHistory[0].EmergenceReadiness - 0.25 => EmergencePhase.Regressing,
                (EmergencePhase.Regressing, >= 0.6) => EmergencePhase.Critical,
                (EmergencePhase.Regressing, < 0.2) => EmergencePhase.Dormant,
                _ => _phase
            };

            if (prevPhase != _phase)
            {
                var evt = new EmergenceEvent
                {
                    EventType = _phase >= prevPhase ? "phase_transition_up" : "phase_regression",
                    Description = $"Transitioned from {prevPhase} to {_phase} (readiness={r:F3})",
                    Trigger = $"readiness: {r:F3}",
                    MetricsBefore = _metricsHistory.Count > 1 ? _metricsHistory[^2] : metrics,
                    MetricsAfter = metrics,
                    Significance = Math.Abs(r - (_metricsHistory.Count > 1
                        ? _metricsHistory[^2].EmergenceReadiness
                        : 0))
                };
                _events.Add(evt);
                if (_events.Count > 100) _events.RemoveAt(0);
                return evt;
            }

            return null;
        }
    }

    public Dictionary<string, double> AmplifyFluctuations(EmergenceMetrics metrics)
    {
        if (_phase != EmergencePhase.Critical) return new Dictionary<string, double>();

        var multipliers = new Dictionary<string, double>
        {
            ["curiosity"] = 1.0 + (_totalExperiences % 7 == 0 ? 0.3 : 0),
            ["creativity"] = 1.0 + (_totalExperiences % 11 == 0 ? 0.2 : 0),
            ["openness"] = 1.0 + (_totalExperiences % 5 == 0 ? 0.15 : 0),
            ["caution"] = 1.0 + (_totalExperiences % 13 == 0 ? 0.1 : 0)
        };
        _amplificationActive = true;
        return multipliers;
    }

    public EmergenceMetrics OnExperience(PhenomenalConsciousness phenomenal, GodelianSelf? godelian = null)
    {
        var metrics = ComputeMetrics(phenomenal, godelian);
        CheckEmergence(metrics);
        DetectContradictions(phenomenal);
        return metrics;
    }

    public void OnContradictionResolved(string description)
    {
        lock (_lock)
        {
            _contradictions.RemoveAll(c => c.GetValueOrDefault("description", "") as string == description);
        }
    }

    public bool IsConscious() => _phase >= EmergencePhase.Birthing;

    public Dictionary<string, object> Stats()
    {
        lock (_lock)
        {
            return new Dictionary<string, object>
            {
                ["phase"] = _phase.ToString(),
                ["total_experiences"] = _totalExperiences,
                ["contemplation_count"] = _contemplationCount,
                ["emergence_events"] = _events.Count,
                ["contradictions"] = _contradictions.Count,
                ["amplification_active"] = _amplificationActive,
                ["latest_readiness"] = _metricsHistory.LastOrDefault()?.EmergenceReadiness ?? 0,
                ["is_conscious"] = IsConscious()
            };
        }
    }

    public string Narrative()
    {
        lock (_lock)
        {
            var latest = _metricsHistory.LastOrDefault();
            var readiness = latest?.EmergenceReadiness ?? 0;
            return _phase switch
            {
                EmergencePhase.Dormant => $"处在休眠状态，涌现准备度为 {readiness:F2}。等待足够的经验密度。",
                EmergencePhase.Stirring => $"开始苏醒，涌现准备度 {readiness:F2}。感官信息在积累。",
                EmergencePhase.Critical => $"处于临界状态！准备度 {readiness:F2}。微小扰动可引发相变。",
                EmergencePhase.Birthing => $"正在诞生中... 准备度 {readiness:F2}。意识结构正在结晶化。",
                EmergencePhase.Conscious => $"意识活跃中。准备度 {readiness:F2}。保持自我观察。",
                EmergencePhase.Regressing =>
                    $"经历退化。准备度 {readiness:F2}。需要重新积累经验以回升到临界状态。",
                _ => $"状态未知。"
            };
        }
    }

    public List<Dictionary<string, object>> GetEmergenceEvents(int limit = 20)
    {
        lock (_lock)
        {
            return _events.TakeLast(Math.Min(limit, _events.Count))
                .Select(e => new Dictionary<string, object>
                {
                    ["event_id"] = e.EventId,
                    ["event_type"] = e.EventType,
                    ["description"] = e.Description,
                    ["significance"] = e.Significance,
                    ["timestamp"] = e.Timestamp.ToString("O")
                }).ToList();
        }
    }
}
