using LTAI.Core.Configuration;
using System.Collections.Concurrent;

namespace LTAI.Agent.Context;

/// <summary>
/// In-memory ring buffer of contrastive feedback for LookaheadProviderSelector.
///
/// Inspired by ContextRL (arXiv 2606.17053): records (query, provider, was_useful)
/// triplets, then groups similar queries into contrastive pairs to calibrate
/// per-domain thresholds. The calibration objective: maximize the margin between
/// useful and not-useful provider activations for similar queries.
///
/// Capacity-bounded (ring buffer) to prevent unbounded memory growth.
/// Query strings are truncated to 128 chars to reduce memory pressure.
/// </summary>
internal static class ContrastiveFeedbackStore
{
    private const int MaxQueryLength = 128;

    public readonly record struct FeedbackEntry(
        long QueryHash,
        string QueryTruncated,
        float[] QueryEmbedding,
        string Provider,
        bool WasUseful,
        DateTime Timestamp);

    private static readonly ConcurrentQueue<FeedbackEntry> Buffer = new();
    private static readonly int MaxEntries = EnvironmentConfig.ContrastiveFeedbackMax;

    private static int _totalDiscarded;
    private static readonly object _calibrateLock = new();
    private static DateTime _lastCalibration = DateTime.MinValue;
    private static readonly TimeSpan CalibrationInterval = TimeSpan.FromMinutes(10);
    private static int _sinceLastCalibration;

    /// <summary>Record one feedback entry. Thread-safe, ring-buffer eviction. Query truncated to 128 chars.</summary>
    public static void Record(string query, float[]? queryEmbedding, string provider, bool wasUseful)
    {
        var hash = ComputeHash64(query);
        var truncated = query.Length <= MaxQueryLength ? query : query[..MaxQueryLength];
        var entry = new FeedbackEntry(hash, truncated, queryEmbedding ?? [], provider, wasUseful, DateTime.UtcNow);
        Buffer.Enqueue(entry);

        // Ring-buffer eviction: keep at most MaxEntries
        while (Buffer.Count > MaxEntries && Buffer.TryDequeue(out _))
            Interlocked.Increment(ref _totalDiscarded);

        // Trigger calibration every N new entries
        var count = Interlocked.Increment(ref _sinceLastCalibration);
        if (count >= 200 && Monitor.TryEnter(_calibrateLock))
        {
            try
            {
                if (DateTime.UtcNow - _lastCalibration >= CalibrationInterval)
                {
                    _lastCalibration = DateTime.UtcNow;
                    _ = CalibrateAsync();
                }
            }
            finally
            {
                _sinceLastCalibration = 0;
                Monitor.Exit(_calibrateLock);
            }
        }
    }

    /// <summary>
    /// Build contrastive pairs from the feedback buffer and return optimal
    /// similarity thresholds per domain.
    ///
    /// Implements the ContextRL contrastive selection objective:
    /// For each provider, find pairs of similar queries (cosine > 0.5) where
    /// one was useful and the other was not → these form contrastive pairs.
    /// The optimal threshold maximizes: P(correct_skip | threshold).
    /// </summary>
    public static Dictionary<string, double> CalibrateThresholds()
    {
        var snapshot = Buffer.ToArray();
        if (snapshot.Length < 20) return new Dictionary<string, double>();

        // Group by provider
        var byProvider = snapshot
            .GroupBy(e => e.Provider, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var thresholds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var (provider, entries) in byProvider)
        {
            if (entries.Count < 6) continue;

            // Compute pairwise cosine similarity for this provider's entries
            var useful = entries.Where(e => e.WasUseful).ToList();
            var notUseful = entries.Where(e => !e.WasUseful).ToList();
            if (useful.Count < 2 || notUseful.Count < 2) continue;

            // Build contrastive pairs: for each useful entry, find the most similar
            // not-useful entry → these form the hardest contrastive pair.
            var maxSim = 0.0;
            var minSim = 1.0;
            foreach (var u in useful)
            {
                foreach (var n in notUseful)
                {
                    if (u.QueryEmbedding.Length == 0 || n.QueryEmbedding.Length == 0) continue;
                    var sim = CosineSimilarity(u.QueryEmbedding, n.QueryEmbedding);
                    if (sim > maxSim) maxSim = sim;
                    if (sim < minSim) minSim = sim;
                }
            }

            // Search for the optimal threshold between minSim and maxSim
            // that maximizes the margin between useful and not-useful distributions.
            // Precompute centroids outside the loop (they're stable across all threshold values).
            double bestThreshold = 0.5;
            double bestScore = double.MinValue;
            var usefulCentroid = FindCentroid(useful.Select(x => x.QueryEmbedding));
            var notUsefulCentroid = FindCentroid(notUseful.Select(x => x.QueryEmbedding));

            for (double t = minSim + 0.01; t < maxSim; t += 0.02)
            {
                var tp = useful.Count(e => CosineSimilarity(e.QueryEmbedding, usefulCentroid) >= t);
                var fp = notUseful.Count(e => CosineSimilarity(e.QueryEmbedding, notUsefulCentroid) >= t);
                var precision = tp + fp > 0 ? (double)tp / (tp + fp) : 0;
                var recall = tp > 0 ? (double)tp / useful.Count : 0;
                var f1 = precision + recall > 0 ? 2 * precision * recall / (precision + recall) : 0;
                if (f1 > bestScore)
                {
                    bestScore = f1;
                    bestThreshold = t;
                }
            }

            thresholds[provider] = bestThreshold;
        }

        return thresholds;
    }

    /// <summary>Callback for applying calibrated thresholds. Set by LookaheadProviderSelector at startup.</summary>
    internal static Action<Dictionary<string, double>>? OnCalibrated;

    private static async Task CalibrateAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                var thresholds = CalibrateThresholds();
                if (thresholds.Count > 0)
                    OnCalibrated?.Invoke(thresholds);
            }
            catch
            {
                // calibration is best-effort; failure is non-critical
            }
        }).ConfigureAwait(false);
    }

    internal static int Count => Buffer.Count;
    internal static int Discarded => _totalDiscarded;

    private static float[] FindCentroid(IEnumerable<float[]> vecs)
    {
        var list = vecs.ToList();
        if (list.Count == 0) return [];
        var dim = list[0].Length;
        var result = new float[dim];
        foreach (var v in list)
            for (int i = 0; i < dim && i < v.Length; i++)
                result[i] += v[i];
        for (int i = 0; i < dim; i++)
            result[i] /= list.Count;
        return result;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        int len = Math.Min(a.Length, b.Length);
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom > 0 ? dot / denom : 0;
    }

    /// <summary>FNV-1a 64-bit hash for query deduplication (zero-allocation).</summary>
    private static long ComputeHash64(string s)
    {
        unchecked
        {
            ulong hash = 14695981039346656037;
            foreach (var c in s)
            {
                hash ^= c;
                hash *= 1099511628211;
            }
            return (long)hash;
        }
    }
}
