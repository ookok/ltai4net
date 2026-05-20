using System.Collections.Concurrent;

namespace LTAI.TreeLLM.Session;

public sealed class MoECompressionBridge
{
    private readonly SegmentedKVCompressor _compressor;

    private static readonly ConcurrentDictionary<ExpertLayer, int> LayerSegmentSizes = new()
    {
        [ExpertLayer.Flash] = 16,
        [ExpertLayer.Hot] = 10,
        [ExpertLayer.Warm] = 6,
        [ExpertLayer.Cold] = 4,
        [ExpertLayer.Deep] = 2
    };

    private static readonly ConcurrentDictionary<ExpertLayer, double> LayerRetentionRates = new()
    {
        [ExpertLayer.Flash] = 1.0,
        [ExpertLayer.Hot] = 0.85,
        [ExpertLayer.Warm] = 0.60,
        [ExpertLayer.Cold] = 0.35,
        [ExpertLayer.Deep] = 0.15
    };

    private const int DefaultSegmentSize = 8;

    public MoECompressionBridge(SegmentedKVCompressor? compressor = null)
    {
        _compressor = compressor ?? new SegmentedKVCompressor();
    }

    public List<Dictionary<string, string>> MoEDrivenCompress(
        List<Dictionary<string, string>> messages,
        MoEQueryResult moeResult,
        int maxTokens,
        Func<string, string>? chatFn = null)
    {
        if (messages.Count <= 3)
            return messages;

        var moeContext = BuildMoEContext(moeResult);
        var enrichedMessages = InjectMoEContext(messages, moeContext);

        var weightedMessages = ApplyMoEWeightedCompression(
            enrichedMessages, moeResult, maxTokens, chatFn);

        return _compressor.Compress(weightedMessages, maxTokens, chatFn);
    }

    public int GetMoESegmentSize(MoEQueryResult moeResult, ExpertLayer defaultLayer = ExpertLayer.Warm)
    {
        if (moeResult.ExpertGates.Count == 0)
            return LayerSegmentSizes.GetValueOrDefault(defaultLayer, DefaultSegmentSize);

        double weightedSize = 0;
        double totalWeight = 0;

        foreach (var (layerKey, gate) in moeResult.ExpertGates)
        {
            if (!Enum.TryParse<ExpertLayer>(layerKey, true, out var layer))
                continue;

            var segmentSize = LayerSegmentSizes.GetValueOrDefault(layer, DefaultSegmentSize);
            weightedSize += segmentSize * gate;
            totalWeight += gate;
        }

        return totalWeight > 0
            ? (int)Math.Round(weightedSize / totalWeight, MidpointRounding.AwayFromZero)
            : DefaultSegmentSize;
    }

    public Dictionary<ExpertLayer, int> AllocateCompressionBudget(
        MoEQueryResult moeResult,
        int totalTokens,
        int systemTokens = 2000)
    {
        var available = Math.Max(1000, totalTokens - systemTokens);
        var budget = new Dictionary<ExpertLayer, int>();

        if (moeResult.ExpertGates.Count == 0)
        {
            budget[ExpertLayer.Flash] = (int)(available * 0.15);
            budget[ExpertLayer.Hot] = (int)(available * 0.35);
            budget[ExpertLayer.Warm] = (int)(available * 0.25);
            budget[ExpertLayer.Cold] = (int)(available * 0.15);
            budget[ExpertLayer.Deep] = (int)(available * 0.10);
            return budget;
        }

        double totalGate = moeResult.ExpertGates.Values.Sum();
        foreach (var (layerKey, gate) in moeResult.ExpertGates)
        {
            if (!Enum.TryParse<ExpertLayer>(layerKey, true, out var layer))
                continue;

            var normalizedGate = gate / totalGate;
            var retention = LayerRetentionRates.GetValueOrDefault(layer, 0.5);
            var layerBudget = (int)(available * normalizedGate * retention);

            budget[layer] = Math.Max(100, layerBudget);
        }

        return budget;
    }

    public List<Dictionary<string, string>> ApplyMoEWeightedCompression(
        List<Dictionary<string, string>> messages,
        MoEQueryResult moeResult,
        int maxTokens,
        Func<string, string>? chatFn = null)
    {
        if (messages.Count <= 5) return messages;

        var queryContext = string.Join(" ", moeResult.All()
            .OrderByDescending(b => b.RetrievalScore)
            .Take(5)
            .Select(b => b.Content));

        return _compressor.WeightedCompress(messages, queryContext, maxTokens, chatFn);
    }

    public string BuildMoECompressionSummary(MoEQueryResult moeResult)
    {
        var parts = new List<string>();

        if (moeResult.ExpertGates.Count > 0)
        {
            parts.Add($"Routing entropy: {moeResult.EntropyEstimate:F3}");
            parts.Add("Layer gates: " + string.Join(", ",
                moeResult.ExpertGates.OrderByDescending(kv => kv.Value)
                    .Select(kv => $"{kv.Key}={kv.Value:F2}")));
        }

        var flashCount = moeResult.FlashResults.Count;
        var hotCount = moeResult.HotResults.Count;
        var warmCount = moeResult.WarmResults.Count;
        var coldCount = moeResult.ColdResults.Count;
        var deepCount = moeResult.DeepResults.Count;

        var segmentSize = GetMoESegmentSize(moeResult);

        parts.Add($"Compression: segment={segmentSize}");
        parts.Add($"Distribution: flash={flashCount} hot={hotCount} warm={warmCount} cold={coldCount} deep={deepCount}");
        parts.Add($"Strategy: flash=full hot=light warm=medium cold=heavy deep=tail-only");

        return string.Join(" | ", parts);
    }

    private static List<Dictionary<string, string>> InjectMoEContext(
        List<Dictionary<string, string>> messages, string moeContext)
    {
        if (string.IsNullOrEmpty(moeContext))
            return messages;

        var result = new List<Dictionary<string, string>>
        {
            new()
            {
                ["role"] = "system",
                ["content"] = $"[MoE Compression Guide: {moeContext}]"
            }
        };

        result.AddRange(messages);
        return result;
    }

    private static string BuildMoEContext(MoEQueryResult moeResult)
    {
        var flashContent = moeResult.FlashResults.Count > 0
            ? "Flash: " + string.Join("; ",
                moeResult.FlashResults.Take(2).Select(b => b.Content[..Math.Min(100, b.Content.Length)]))
            : "";

        var hotContent = moeResult.HotResults.Count > 0
            ? "Hot: " + string.Join("; ",
                moeResult.HotResults.Take(3).Select(b => b.Content[..Math.Min(80, b.Content.Length)]))
            : "";

        var deepContent = moeResult.DeepResults.Count > 0
            ? "Deep: " + string.Join("; ",
                moeResult.DeepResults.Take(2).Select(b => b.Content[..Math.Min(80, b.Content.Length)]))
            : "";

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(flashContent)) parts.Add(flashContent);
        if (!string.IsNullOrEmpty(hotContent)) parts.Add(hotContent);
        if (!string.IsNullOrEmpty(deepContent)) parts.Add(deepContent);

        return parts.Count > 0 ? string.Join(" | ", parts) : "";
    }
}
