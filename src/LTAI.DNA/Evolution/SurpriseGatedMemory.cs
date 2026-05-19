using LTAI.DNA.Models;

namespace LTAI.DNA.Evolution;

public sealed class SurpriseGatedMemory
{
    private readonly Dictionary<string, double> _expectedPatterns = new();
    private readonly List<SurpriseSignal> _recentSignals = new();
    private readonly Dictionary<string, string> _fastBuffer = new();
    private int _evolutionCount;
    private int _bypassCount;
    private readonly object _lock = new();
    private const double SurpriseThreshold = 0.4;
    private const int MaxPatterns = 5000;
    private const int MaxBufferSize = 500;

    public SurpriseGatedMemory() { }

    public SurpriseSignal Evaluate(string content, string? context = null)
    {
        lock (_lock)
        {
            var surprise = ComputeSurprise(content);
            var utility = ComputeUtility(content, context);
            var rpe = surprise * 0.6 + utility * 0.4;
            var shouldEvolve = rpe > SurpriseThreshold;

            var reason = shouldEvolve
                ? $"High RPE ({rpe:F2}): surprise={surprise:F2}, utility={utility:F2}"
                : $"Low RPE ({rpe:F2}): bypassing";

            var signal = new SurpriseSignal
            {
                SurpriseScore = surprise,
                UtilityScore = utility,
                RPE = rpe,
                ShouldEvolve = shouldEvolve,
                Reason = reason
            };

            _recentSignals.Add(signal);
            if (_recentSignals.Count > 50) _recentSignals.RemoveAt(0);

            return signal;
        }
    }

    private double ComputeSurprise(string content)
    {
        var ngrams = ExtractNgrams(content, 3);
        if (ngrams.Length == 0) return 0.3;

        double overlap = 0;
        foreach (var ng in ngrams)
            if (_expectedPatterns.TryGetValue(ng, out var w))
                overlap += w;

        var avgOverlap = overlap / ngrams.Length;
        var surprise = 1 - avgOverlap;

        if (avgOverlap < 0.05) surprise += 0.3;

        return Math.Clamp(surprise, 0, 1);
    }

    private double ComputeUtility(string content, string? context)
    {
        double utility = 0;

        var entities = CountNovelEntities(content);
        utility += entities * 0.05;

        var numbers = System.Text.RegularExpressions.Regex.Matches(content, @"\d+").Count;
        if (numbers >= 3) utility += 0.1;

        if (content.Length > 500) utility += 0.1;

        var words = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0)
        {
            var uniqueRatio = (double)words.Distinct().Count() / words.Length;
            if (uniqueRatio > 0.8) utility += 0.15;
        }

        return Math.Min(1.0, utility);
    }

    private int CountNovelEntities(string content)
    {
        var patterns = new[]
        {
            @"\b[A-Z][a-z]+\b",
            @"https?://\S+",
            @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}"
        };
        var count = 0;
        foreach (var p in patterns)
            count += System.Text.RegularExpressions.Regex.Matches(content, p).Count;
        return count;
    }

    private static string[] ExtractNgrams(string content, int n)
    {
        var words = content.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < n) return words;
        var ngrams = new List<string>(words.Length - n + 1);
        for (int i = 0; i <= words.Length - n; i++)
            ngrams.Add(string.Join(" ", words[i..(i + n)]));
        return ngrams.ToArray();
    }

    public void UpdateExpectations(string content, bool success)
    {
        lock (_lock)
        {
            var ngrams = ExtractNgrams(content, 3);
            foreach (var ng in ngrams)
            {
                if (_expectedPatterns.TryGetValue(ng, out var w))
                    _expectedPatterns[ng] = Math.Clamp(w + (success ? 0.08 : -0.03), 0, 10);
                else if (success)
                    _expectedPatterns[ng] = 0.08;
            }

            if (_expectedPatterns.Count > MaxPatterns)
            {
                var weakKeys = _expectedPatterns.Where(kv => kv.Value < 0.05).Select(kv => kv.Key).ToList();
                foreach (var key in weakKeys) _expectedPatterns.Remove(key);
            }
        }
    }

    public string GetRoutingDecision(string content)
    {
        var signal = Evaluate(content);
        return signal.ShouldEvolve ? "evolve_path" : "fast_path";
    }

    public async Task<Dictionary<string, object>> ProcessContent(string content, object? memorySystem = null,
        object? consciousness = null)
    {
        var signal = Evaluate(content);
        if (!signal.ShouldEvolve)
        {
            lock (_lock)
            {
                var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(content)));
                _fastBuffer[key] = content;
                if (_fastBuffer.Count > MaxBufferSize)
                {
                    var firstKey = _fastBuffer.Keys.First();
                    _fastBuffer.Remove(firstKey);
                }
                _bypassCount++;
            }

            return new Dictionary<string, object>
            {
                ["action"] = "bypass",
                ["rpe"] = signal.RPE,
                ["in_buffer"] = true,
                ["buffer_size"] = _fastBuffer.Count
            };
        }

        lock (_lock) { _evolutionCount++; }
        UpdateExpectations(content, true);

        return new Dictionary<string, object>
        {
            ["action"] = "evolve",
            ["rpe"] = signal.RPE,
            ["reason"] = signal.Reason,
            ["evolutions"] = _evolutionCount
        };
    }

    public List<Dictionary<string, object>> IterativeReconsolidation(string content, double targetAccuracy = 0.95,
        int maxIterations = 10)
    {
        var results = new List<Dictionary<string, object>>();
        for (int i = 0; i < maxIterations; i++)
        {
            var signal = Evaluate(content);
            UpdateExpectations(content, signal.SurpriseScore < SurpriseThreshold);
            results.Add(new Dictionary<string, object>
            {
                ["iteration"] = i,
                ["surprise"] = signal.SurpriseScore,
                ["rpe"] = signal.RPE,
                ["converged"] = signal.SurpriseScore < 1 - targetAccuracy
            });
            if (signal.SurpriseScore < 1 - targetAccuracy) break;
        }

        return results;
    }

    public Dictionary<string, object> Stats()
    {
        lock (_lock)
        {
            return new Dictionary<string, object>
            {
                ["total_processed"] = _evolutionCount + _bypassCount,
                ["evolutions"] = _evolutionCount,
                ["bypasses"] = _bypassCount,
                ["bypass_ratio"] = _evolutionCount + _bypassCount > 0
                    ? (double)_bypassCount / (_evolutionCount + _bypassCount)
                    : 0,
                ["patterns"] = _expectedPatterns.Count,
                ["buffer_size"] = _fastBuffer.Count,
                ["recent_rpe"] = _recentSignals.LastOrDefault()?.RPE ?? 0
            };
        }
    }

    public void WarmBuffer(List<string> contents)
    {
        foreach (var content in contents)
        {
            var signal = Evaluate(content);
            if (!signal.ShouldEvolve)
            {
                var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(content)));
                lock (_lock) { _fastBuffer[key] = content; }
            }
        }
    }
}
