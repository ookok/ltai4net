using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

public sealed record ActionFingerprint
{
    public string Action { get; init; } = "";
    public string InputHash { get; init; } = "";
    public float[]? Embedding { get; init; }
    public long Timestamp { get; init; }
}

public sealed record TrapResult
{
    public bool Trapped { get; init; }
    public string TrapType { get; init; } = ""; // exact_repeat, semantic_loop, cycle, idle
    public string Reason { get; init; } = "";
    public int RepeatCount { get; init; }
    public double Severity { get; init; }
    public string[] SuggestedActions { get; init; } = Array.Empty<string>();
    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;
}

public sealed class LoopTrapDetector
{
    private readonly int _historySize;
    private readonly float _cosineThreshold;
    private readonly int _exactRepeatThreshold;
    private readonly int _cycleWindowSize;
    private readonly int _idleThreshold;
    private readonly ConcurrentQueue<ActionFingerprint> _history = new();
    private readonly ConcurrentDictionary<string, int> _exactCounts = new();
    private int _totalChecks;
    private int _trapsDetected;
    private int _trapsBroken;
    private readonly ILogger<LoopTrapDetector> _logger;

    private static readonly SHA256 _sha = SHA256.Create();

    public int TotalChecks => _totalChecks;
    public int TrapsDetected => _trapsDetected;
    public int TrapsBroken => _trapsBroken;
    public double TrapRate => _totalChecks > 0 ? (double)_trapsDetected / _totalChecks : 0;

    public LoopTrapDetector(
        int historySize = 32,
        float cosineThreshold = 0.92f,
        int exactRepeatThreshold = 3,
        int cycleWindowSize = 8,
        int idleThreshold = 50,
        ILogger<LoopTrapDetector>? logger = null)
    {
        _historySize = historySize;
        _cosineThreshold = cosineThreshold;
        _exactRepeatThreshold = exactRepeatThreshold;
        _cycleWindowSize = cycleWindowSize;
        _idleThreshold = idleThreshold;
        _logger = logger ?? NullLogger<LoopTrapDetector>.Instance;
    }

    public TrapResult Check(string action, string input, float[]? embedding = null)
    {
        Interlocked.Increment(ref _totalChecks);

        var fingerprint = new ActionFingerprint
        {
            Action = action,
            InputHash = HashInput(input),
            Embedding = embedding,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        _history.Enqueue(fingerprint);
        while (_history.Count > _historySize)
            _history.TryDequeue(out _);

        var exactCheck = CheckExactRepeat(fingerprint);
        if (exactCheck.Trapped)
        {
            Interlocked.Increment(ref _trapsDetected);
            return exactCheck;
        }

        if (embedding != null)
        {
            var semanticCheck = CheckSemanticLoop(fingerprint);
            if (semanticCheck.Trapped)
            {
                Interlocked.Increment(ref _trapsDetected);
                return semanticCheck;
            }
        }

        var cycleCheck = CheckCyclePattern();
        if (cycleCheck.Trapped)
        {
            Interlocked.Increment(ref _trapsDetected);
            return cycleCheck;
        }

        var idleCheck = CheckIdleLoop();
        if (idleCheck.Trapped)
        {
            Interlocked.Increment(ref _trapsDetected);
            return idleCheck;
        }

        return new TrapResult { Trapped = false };
    }

    public void RecordBreak(string strategy)
    {
        Interlocked.Increment(ref _trapsBroken);
        _logger.LogInformation("Loop trap broken via {Strategy} (total breaks: {Count})", strategy, _trapsBroken);
    }

    private TrapResult CheckExactRepeat(ActionFingerprint current)
    {
        var key = $"{current.Action}|{current.InputHash}";
        _exactCounts.AddOrUpdate(key, 1, (_, v) => v + 1);
        var count = _exactCounts.GetValueOrDefault(key, 0);

        if (count >= _exactRepeatThreshold)
        {
            return new TrapResult
            {
                Trapped = true,
                TrapType = "exact_repeat",
                Reason = $"Identical action+input repeated {count} times",
                RepeatCount = count,
                Severity = Math.Min(1.0, count / (double)_exactRepeatThreshold),
                SuggestedActions = new[]
                {
                    "route_up",                         // escalate to next tier
                    "random_perturb",                   // inject random token
                    "strategy_rotate:different_approach" // ask LLM to try different approach
                }
            };
        }

        return new TrapResult { Trapped = false };
    }

    private TrapResult CheckSemanticLoop(ActionFingerprint current)
    {
        var recentEmbeddings = _history
            .Where(h => h.Embedding != null)
            .Select(h => h.Embedding!)
            .TakeLast(_exactRepeatThreshold + 2)
            .ToList();

        if (recentEmbeddings.Count < _exactRepeatThreshold)
            return new TrapResult { Trapped = false };

        var currentEmbedding = current.Embedding!;
        int similarCount = 0;

        foreach (var emb in recentEmbeddings)
        {
            if (emb == currentEmbedding) continue;
            var similarity = CosineSimilarity(currentEmbedding, emb);
            if (similarity >= _cosineThreshold)
                similarCount++;
        }

        if (similarCount >= _exactRepeatThreshold)
        {
            return new TrapResult
            {
                Trapped = true,
                TrapType = "semantic_loop",
                Reason = $"{similarCount} semantically similar actions detected (threshold={_cosineThreshold:F2})",
                RepeatCount = similarCount,
                Severity = Math.Min(1.0, similarCount / (double)_exactRepeatThreshold),
                SuggestedActions = new[]
                {
                    "temperature_bump",         // increase LLM temperature
                    "divergent_prompt",         // ask for a fundamentally different approach
                    "route_up"                  // escalate to stronger model
                }
            };
        }

        return new TrapResult { Trapped = false };
    }

    private TrapResult CheckCyclePattern()
    {
        var recent = _history.TakeLast(_cycleWindowSize).ToList();
        if (recent.Count < _cycleWindowSize)
            return new TrapResult { Trapped = false };

        var fingerprints = recent.Select(f => $"{f.Action}|{f.InputHash}").ToList();

        for (int period = 2; period <= _cycleWindowSize / 2; period++)
        {
            bool isCycle = true;
            for (int offset = 0; offset < _cycleWindowSize - period; offset++)
            {
                if (fingerprints[offset] != fingerprints[offset + period])
                {
                    isCycle = false;
                    break;
                }
            }

            if (isCycle)
            {
                return new TrapResult
                {
                    Trapped = true,
                    TrapType = "cycle",
                    Reason = $"Cycle detected with period={period}",
                    RepeatCount = _cycleWindowSize / period,
                    Severity = 0.8,
                    SuggestedActions = new[]
                    {
                        "force_explore",         // force exploration budget
                        "strategy_rotate:random", // pick random alternative strategy
                        "pause_and_reflect"      // meta-cognitive pause
                    }
                };
            }
        }

        return new TrapResult { Trapped = false };
    }

    private TrapResult CheckIdleLoop()
    {
        var recent = _history.TakeLast(_idleThreshold).ToList();
        if (recent.Count < _idleThreshold)
            return new TrapResult { Trapped = false };

        var uniqueActions = new HashSet<string>();
        foreach (var f in recent)
            uniqueActions.Add($"{f.Action}|{f.InputHash}");

        double diversity = (double)uniqueActions.Count / recent.Count;

        if (diversity < 0.15 && recent.Count >= _idleThreshold)
        {
            return new TrapResult
            {
                Trapped = true,
                TrapType = "idle",
                Reason = $"Low action diversity ({diversity:F2}) over {_idleThreshold} steps",
                RepeatCount = recent.Count - uniqueActions.Count,
                Severity = 1.0 - diversity,
                SuggestedActions = new[]
                {
                    "expand_scope",         // broaden the task scope
                    "human_escalate",       // request human intervention
                    "abort_and_restart"     // abort and restart with fresh context
                }
            };
        }

        return new TrapResult { Trapped = false };
    }

    private static string HashInput(string input)
    {
        var bytes = _sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16];
    }

    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return -1f;
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        if (normA == 0 || normB == 0) return 0;
        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    public void Reset()
    {
        while (_history.TryDequeue(out _)) { }
        _exactCounts.Clear();
    }
}
