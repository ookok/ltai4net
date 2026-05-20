using System.Collections.Concurrent;
using System.Diagnostics;

namespace LTAI.Economy;

public sealed record HardwareProfile(
    string CandidateId,
    double TotalLatencyMs,
    double ComputeLatencyMs,
    double MemoryLatencyMs,
    double MemoryBandwidthGbS,
    double ComputeUtilization,
    double MemoryMb,
    double FlopsPerSecond,
    double VectorRegUtilization,
    double ScalarRegUtilization,
    Dictionary<string, double> DetailedMetrics,
    DateTime ProfiledAt)
{
    public double MemoryBoundRatio => MemoryLatencyMs / Math.Max(1, TotalLatencyMs);
    public double ComputeBoundRatio => ComputeLatencyMs / Math.Max(1, TotalLatencyMs);
    public bool IsMemoryBound => MemoryBoundRatio > 0.6;
    public bool IsComputeBound => ComputeBoundRatio > 0.6;
}

public sealed record ProfilingConfig(
    int WarmupRuns = 3,
    int MeasurementRuns = 10,
    double OutlierStdDevThreshold = 2.0,
    bool CaptureDetailed = true,
    int TimeoutMs = 30000)
{
    public static ProfilingConfig Default => new();
}

public sealed class HardwareProfiler
{
    private readonly ProfilingConfig _config;
    private readonly ConcurrentDictionary<string, HardwareProfile> _profiles = new();
    private readonly ConcurrentQueue<HardwareProfile> _history = new();
    private readonly Stopwatch _globalClock = Stopwatch.StartNew();

    public event Action<string, HardwareProfile>? OnProfileComplete;
    public event Action<string, string>? OnProfileWarning;

    public HardwareProfiler(ProfilingConfig? config = null)
    {
        _config = config ?? ProfilingConfig.Default;
    }

    public async Task<(double latencyMs, Dictionary<string, double> metrics)> ProfileAsync(
        EvolutionCandidate candidate,
        CancellationToken ct = default)
    {
        try
        {
            var latencySamples = new List<double>();
            double compLatencySum = 0;
            double memLatencySum = 0;
            double memBwSum = 0;
            double computeUtilSum = 0;
            double flopsSum = 0;
            int validSamples = 0;

            for (int i = 0; i < _config.WarmupRuns + _config.MeasurementRuns; i++)
            {
                if (ct.IsCancellationRequested) break;

                var sw = Stopwatch.StartNew();
                var (compMs, memMs) = EstimateLatencyBreakdown(candidate);
                sw.Stop();

                if (i >= _config.WarmupRuns)
                {
                    latencySamples.Add(sw.Elapsed.TotalMilliseconds);
                    compLatencySum += compMs;
                    memLatencySum += memMs;
                    validSamples++;
                }

                memBwSum += EstimateMemoryBandwidth(candidate);
                computeUtilSum += EstimateComputeUtilization(candidate);
                flopsSum += EstimateFlops(candidate);
            }

            if (validSamples == 0) validSamples = 1;

            var filteredLatencies = FilterOutliers(latencySamples);
            var avgLatencyMs = filteredLatencies.Count > 0
                ? filteredLatencies.Average()
                : latencySamples.DefaultIfEmpty(100).Average();

            var avgCompMs = compLatencySum / validSamples;
            var avgMemMs = memLatencySum / validSamples;
            var avgMemBw = memBwSum / _config.MeasurementRuns;
            var avgComputeUtil = computeUtilSum / _config.MeasurementRuns;
            var avgFlops = flopsSum / _config.MeasurementRuns;

            var vRegUtil = EstimateVectorRegUtilization(candidate);
            var sRegUtil = EstimateScalarRegUtilization(candidate);
            var estimatedMemMb = EstimateMemoryFootprint(candidate);

            var profile = new HardwareProfile(
                CandidateId: candidate.Id,
                TotalLatencyMs: avgLatencyMs,
                ComputeLatencyMs: avgCompMs,
                MemoryLatencyMs: avgMemMs,
                MemoryBandwidthGbS: avgMemBw,
                ComputeUtilization: avgComputeUtil,
                MemoryMb: estimatedMemMb,
                FlopsPerSecond: avgFlops,
                VectorRegUtilization: vRegUtil,
                ScalarRegUtilization: sRegUtil,
                DetailedMetrics: _config.CaptureDetailed
                    ? CaptureDetailedMetrics(candidate, avgLatencyMs, avgComputeUtil, estimatedMemMb)
                    : new(),
                ProfiledAt: DateTime.UtcNow);

            _profiles[candidate.Id] = profile;
            _history.Enqueue(profile);
            while (_history.Count > 1000) _history.TryDequeue(out _);

            OnProfileComplete?.Invoke(candidate.Id, profile);

            if (profile.IsMemoryBound)
                OnProfileWarning?.Invoke(candidate.Id,
                    $"Memory bound: {profile.MemoryBoundRatio:P0} of time in memory ops");

            if (profile.ComputeUtilization < 0.2)
                OnProfileWarning?.Invoke(candidate.Id,
                    $"Low compute utilization: {profile.ComputeUtilization:P0}");

            var metrics = new Dictionary<string, double>
            {
                ["latency_ms"] = avgLatencyMs,
                ["compute_ms"] = avgCompMs,
                ["memory_ms"] = avgMemMs,
                ["memory_bw_gbs"] = avgMemBw,
                ["compute_util"] = avgComputeUtil,
                ["memory_mb"] = estimatedMemMb,
                ["flops"] = avgFlops,
                ["vreg_util"] = vRegUtil,
                ["sreg_util"] = sRegUtil,
                ["memory_bound"] = profile.MemoryBoundRatio,
                ["compute_bound"] = profile.ComputeBoundRatio
            };

            return (avgLatencyMs, metrics);
        }
        catch (Exception ex)
        {
            OnProfileWarning?.Invoke(candidate.Id, $"Profiling failed: {ex.Message}");
            return (double.MaxValue, new Dictionary<string, double> { ["error"] = -1 });
        }
    }

    public HardwareProfile? GetProfile(string candidateId)
    {
        _profiles.TryGetValue(candidateId, out var profile);
        return profile;
    }

    public List<HardwareProfile> GetHistory(int count = 100)
    {
        return _history.ToList().TakeLast(count).ToList();
    }

    public Dictionary<string, double> GetGlobalStats()
    {
        var all = _history.ToList();
        if (all.Count == 0)
            return new Dictionary<string, double> { ["no_samples"] = 0 };

        return new Dictionary<string, double>
        {
            ["total_profiles"] = all.Count,
            ["avg_latency_ms"] = all.Average(p => p.TotalLatencyMs),
            ["min_latency_ms"] = all.Min(p => p.TotalLatencyMs),
            ["max_latency_ms"] = all.Max(p => p.TotalLatencyMs),
            ["avg_compute_util"] = all.Average(p => p.ComputeUtilization),
            ["avg_memory_bw"] = all.Average(p => p.MemoryBandwidthGbS),
            ["avg_memory_mb"] = all.Average(p => p.MemoryMb),
            ["memory_bound_pct"] = (double)all.Count(p => p.IsMemoryBound) / all.Count,
            ["compute_bound_pct"] = (double)all.Count(p => p.IsComputeBound) / all.Count
        };
    }

    private static (double compMs, double memMs) EstimateLatencyBreakdown(EvolutionCandidate candidate)
    {
        var code = candidate.Code;
        var lines = code.Split('\n');

        int loadOps = 0, storeOps = 0, computeOps = 0;
        foreach (var line in lines)
        {
            var trimmed = line.Trim().ToLower();
            if (trimmed.StartsWith("#") || trimmed.StartsWith("//") || string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (trimmed.Contains("load") || trimmed.Contains("read") || trimmed.Contains("get") ||
                trimmed.Contains("gather") || trimmed.Contains("[") && trimmed.Contains("]"))
                loadOps++;

            if (trimmed.Contains("store") || trimmed.Contains("write") || trimmed.Contains("set") ||
                trimmed.Contains("scatter") || trimmed.Contains("=") && trimmed.Contains("["))
                storeOps++;

            if (trimmed.Contains("+") || trimmed.Contains("*") || trimmed.Contains("-") || trimmed.Contains("/") ||
                trimmed.Contains("dot") || trimmed.Contains("mul") || trimmed.Contains("matmul") ||
                trimmed.Contains("sum") || trimmed.Contains("exp") || trimmed.Contains("log"))
                computeOps++;
        }

        var totalOps = loadOps + storeOps + computeOps;
        if (totalOps == 0) return (1, 1);

        var memCostPerOp = 50.0;
        var compCostPerOp = 10.0;
        var memMs = (loadOps + storeOps) * memCostPerOp / 1000.0;
        var compMs = computeOps * compCostPerOp / 1000.0;

        return (compMs, memMs);
    }

    private static double EstimateMemoryBandwidth(EvolutionCandidate candidate)
    {
        var code = candidate.Code;
        var dataElements = System.Text.RegularExpressions.Regex.Matches(code, @"\b(float|int|double|short|byte)\[(\d+)\]");
        long totalBytes = 0;

        foreach (System.Text.RegularExpressions.Match m in dataElements)
        {
            if (long.TryParse(m.Groups[2].Value, out var count))
            {
                var typeSize = m.Groups[1].Value switch
                {
                    "double" => 8,
                    "float" or "int" => 4,
                    "short" => 2,
                    "byte" => 1,
                    _ => 4
                };
                totalBytes += count * typeSize;
            }
        }

        totalBytes = Math.Max(totalBytes, candidate.Code.Length * 4);
        return totalBytes / (1024.0 * 1024.0 * 1024.0) * 100;
    }

    private static double EstimateComputeUtilization(EvolutionCandidate candidate)
    {
        var code = candidate.Code;
        var totalLines = code.Split('\n').Length;
        if (totalLines == 0) return 0;

        int simdOps = System.Text.RegularExpressions.Regex.Matches(code, @"\b(vectorize|simd|parallel|vmap|pmap)\b").Count;
        int loopOps = System.Text.RegularExpressions.Regex.Matches(code, @"\b(for|while|scan)\b").Count;
        int mathOps = System.Text.RegularExpressions.Regex.Matches(code, @"[*+/]|dot|mul|add|sum").Count;

        double utilization = Math.Min(1.0, (simdOps * 0.3 + loopOps * 0.2 + mathOps * 0.01));
        utilization += candidate.Code.Contains("[" + "128") ? 0.2 : 0;
        utilization += candidate.Code.Contains("(8,128)") ? 0.3 : 0;
        utilization += candidate.Code.Contains("unroll") ? 0.1 : 0;

        return Math.Min(1.0, utilization);
    }

    private static double EstimateVectorRegUtilization(EvolutionCandidate candidate)
    {
        var hasVRegAlignment = candidate.Code.Contains("(8,128)") ||
                               candidate.Code.Contains("(128,") ||
                               candidate.Code.Contains(",128)") ||
                               candidate.Code.Contains("VReg");

        if (hasVRegAlignment) return 0.8 + Random.Shared.NextDouble() * 0.2;

        var hasBatchDim = candidate.Code.Contains("batch") || candidate.Code.Contains("shape[0]");
        return hasBatchDim ? 0.4 + Random.Shared.NextDouble() * 0.3 : 0.2;
    }

    private static double EstimateScalarRegUtilization(EvolutionCandidate candidate)
    {
        var scalarOps = System.Text.RegularExpressions.Regex.Matches(candidate.Code, @"\b(a\s*=|x\s*=|val\s*=|scalar)").Count;
        return scalarOps > 0 ? 0.3 + Random.Shared.NextDouble() * 0.2 : 0.1;
    }

    private static double EstimateMemoryFootprint(EvolutionCandidate candidate)
    {
        var sizeMatches = System.Text.RegularExpressions.Regex.Matches(candidate.Code, @"\b(\d+)\s*\*\s*(\d+)\s*\*\s*(\d+)");
        long totalBytes = candidate.Code.Length * 4;

        foreach (System.Text.RegularExpressions.Match m in sizeMatches)
        {
            if (long.TryParse(m.Groups[1].Value, out var d1) &&
                long.TryParse(m.Groups[2].Value, out var d2) &&
                long.TryParse(m.Groups[3].Value, out var d3))
            {
                totalBytes += d1 * d2 * d3 * 4;
            }
        }

        return totalBytes / (1024.0 * 1024.0);
    }

    private static double EstimateFlops(EvolutionCandidate candidate)
    {
        var mulCount = candidate.Code.Count(c => c == '*');
        var addCount = candidate.Code.Count(c => c == '+');
        var expCount = System.Text.RegularExpressions.Regex.Matches(candidate.Code, @"\b(exp|log|sin|cos|tan)\b").Count;

        return (mulCount + addCount) * 1_000_000.0 + expCount * 10_000_000.0;
    }

    private static List<double> FilterOutliers(List<double> samples)
    {
        if (samples.Count < 3) return samples;

        var mean = samples.Average();
        var stdDev = Math.Sqrt(samples.Average(s => (s - mean) * (s - mean)));
        var threshold = 2.0 * stdDev;

        return samples.Where(s => Math.Abs(s - mean) <= Math.Max(threshold, mean * 0.5)).ToList();
    }

    private static Dictionary<string, double> CaptureDetailedMetrics(
        EvolutionCandidate candidate, double avgLatencyMs, double computeUtil, double memMb)
    {
        return new Dictionary<string, double>
        {
            ["code_length"] = candidate.Code.Length,
            ["line_count"] = candidate.Code.Split('\n').Length,
            ["instruction_count"] = candidate.Code.Count(c => c is '\n' or ';'),
            ["branch_count"] = System.Text.RegularExpressions.Regex.Matches(candidate.Code, @"\b(if|else|switch)\b").Count,
            ["loop_count"] = System.Text.RegularExpressions.Regex.Matches(candidate.Code, @"\b(for|while)\b").Count,
            ["function_count"] = System.Text.RegularExpressions.Regex.Matches(candidate.Code, @"\bdef\s+\w+").Count,
            ["memory_load_count"] = System.Text.RegularExpressions.Regex.Matches(
                candidate.Code, @"\b(load|read|get|gather)\b").Count,
            ["memory_store_count"] = System.Text.RegularExpressions.Regex.Matches(
                candidate.Code, @"\b(store|write|set|scatter)\b").Count,
            ["compute_op_count"] = System.Text.RegularExpressions.Regex.Matches(
                candidate.Code, @"[+*/-]|dot|mul|add|sum").Count,
            ["latency_per_instruction"] = avgLatencyMs / Math.Max(1,
                System.Text.RegularExpressions.Regex.Matches(candidate.Code, @"\n").Count),
            ["compute_efficiency"] = computeUtil,
            ["memory_efficiency"] = memMb > 0 ? candidate.Code.Length / memMb / 1024.0 : 0,
            ["timestamp_ticks"] = Stopwatch.GetTimestamp()
        };
    }
}
