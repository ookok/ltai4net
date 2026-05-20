using System.Collections.Concurrent;
using System.Text.Json;

namespace LTAI.TreeLLM.Session;

public enum ExpertLayer { Flash, Hot, Warm, Cold, Deep }

public sealed class MoEMemoryBlock
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Content { get; set; } = "";
    public ExpertLayer Layer { get; set; } = ExpertLayer.Warm;
    public double Relevance { get; set; } = 0.5;
    public double Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public double LastAccess { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public int AccessCount { get; set; }
    public string SessionId { get; set; } = "";
    public double TaskCriticality { get; set; }
    public List<string> Tags { get; set; } = new();
    public double DataValueDensity { get; set; } = 0.5;
    public double AlibiSlope { get; set; } = 1.0;
    public double[]? Embedding { get; set; }
    public double RetrievalScore { get; set; }

    public double DecayedRelevance(double currentTime)
    {
        var hours = (currentTime - LastAccess) / 3600.0;
        return Relevance * Math.Exp(-0.015 * hours);
    }

    public double[] GetTokenVector(int dim = 64)
    {
        if (Embedding != null && Embedding.Length == dim) return Embedding;
        var vec = new double[dim];
        var hash = (uint)Content.GetHashCode();
        var rng = new Random((int)hash);
        for (int i = 0; i < dim; i++)
            vec[i] = (rng.NextDouble() - 0.5) * 2.0;
        var norm = Math.Sqrt(vec.Sum(v => v * v));
        if (norm > 1e-8)
            for (int i = 0; i < dim; i++) vec[i] /= norm;
        return vec;
    }
}

public sealed class MoEQueryResult
{
    public List<MoEMemoryBlock> FlashResults { get; set; } = new();
    public List<MoEMemoryBlock> HotResults { get; set; } = new();
    public List<MoEMemoryBlock> WarmResults { get; set; } = new();
    public List<MoEMemoryBlock> ColdResults { get; set; } = new();
    public List<MoEMemoryBlock> DeepResults { get; set; } = new();
    public List<MoEMemoryBlock> Activated { get; set; } = new();
    public Dictionary<string, double> ExpertWeights { get; set; } = new();
    public Dictionary<string, double> ExpertGates { get; set; } = new();
    public long LatencyMs { get; set; }
    public double EntropyEstimate { get; set; }

    public IEnumerable<MoEMemoryBlock> All()
    {
        foreach (var b in FlashResults) yield return b;
        foreach (var b in HotResults) yield return b;
        foreach (var b in WarmResults) yield return b;
        foreach (var b in ColdResults) yield return b;
        foreach (var b in DeepResults) yield return b;
    }

    public List<MoEMemoryBlock> TopK(int k = 5)
    {
        return All()
            .OrderByDescending(b => b.RetrievalScore)
            .Take(k).ToList();
    }
}

public sealed class ContextMoE
{
    private static readonly ConcurrentDictionary<string, ContextMoE> _sessions = new();
    private const int MaxHot = 7, MaxWarm = 50, MaxCold = 200;
    private const int EmbeddingDim = 64;

    private readonly List<MoEMemoryBlock> _hot = new();
    private readonly Dictionary<string, MoEMemoryBlock> _warm = new();
    private readonly Dictionary<string, MoEMemoryBlock> _cold = new();
    private readonly Dictionary<string, MoEMemoryBlock> _deep = new();
    private readonly Dictionary<string, MoEMemoryBlock> _flash = new();
    private readonly MoERouter _router = new();
    private readonly SpreadingActivation _activation = new();
    private readonly string _sessionId, _dataDir;

    public static ContextMoE GetSession(string sessionId)
    {
        return _sessions.GetOrAdd(sessionId, _ => new ContextMoE(sessionId));
    }

    private ContextMoE(string sessionId)
    {
        _sessionId = sessionId;
        _dataDir = global::System.IO.Path.Combine(".livingtree", "context_moe");
        global::System.IO.Directory.CreateDirectory(_dataDir);
        Load();
    }

    public async Task<MoEQueryResult> QueryAsync(string query, string taskType = "general")
    {
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        var result = new MoEQueryResult();
        var queryVec = GetQueryVector(query);
        var current = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var (weights, gates) = _router.GetWeightsAndGates(taskType, query, queryVec);
        result.ExpertWeights = weights;
        result.ExpertGates = gates;
        result.EntropyEstimate = ComputeRoutingEntropy(gates);

        var allBlocks = _flash.Values.Cast<MoEMemoryBlock>()
            .Concat(_hot).Concat(_warm.Values).Concat(_cold.Values).Concat(_deep.Values)
            .ToList();

        foreach (var block in allBlocks)
        {
            block.RetrievalScore = CosineSimilarity(queryVec, block.GetTokenVector(EmbeddingDim))
                * block.DecayedRelevance(current) * block.DataValueDensity;

            var memoryBoost = _activation.GetActivationBoost(block.Id, query);
            block.RetrievalScore *= (1.0 + memoryBoost * 0.3);
        }

        var gatedBlockCount = (int)(allBlocks.Count * 0.4 * (1.0 - result.EntropyEstimate * 0.5));

        result.FlashResults = allBlocks
            .Where(b => b.Layer == ExpertLayer.Flash)
            .OrderByDescending(b => b.RetrievalScore).Take(3).ToList();
        result.HotResults = allBlocks
            .Where(b => b.Layer == ExpertLayer.Hot)
            .OrderByDescending(b => b.RetrievalScore).Take(Math.Min(5, gatedBlockCount)).ToList();
        result.WarmResults = allBlocks
            .Where(b => b.Layer == ExpertLayer.Warm)
            .OrderByDescending(b => b.RetrievalScore).Take(Math.Min(5, gatedBlockCount)).ToList();
        result.ColdResults = allBlocks
            .Where(b => b.Layer == ExpertLayer.Cold)
            .OrderByDescending(b => b.RetrievalScore).Take(5).ToList();
        result.DeepResults = allBlocks
            .Where(b => b.Layer == ExpertLayer.Deep)
            .OrderByDescending(b => b.RetrievalScore).Take(3).ToList();

        result.Activated = _activation.Spread(query, result.All().ToList());
        foreach (var b in result.HotResults) b.AccessCount++;

        _router.RecordRetrieval(taskType, result);
        result.LatencyMs = sw.ElapsedMilliseconds;
        return result;
    }

    public string BuildEnriched(string userInput, MoEQueryResult result)
    {
        var parts = new List<string>();

        if (result.ExpertGates.Count > 0)
        {
            var gateInfo = string.Join(", ", result.ExpertGates
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}:{kv.Value:F2}"));
            parts.Add($"## Expert Gates: {gateInfo}");
        }

        var topK = result.TopK(5);
        if (topK.Count > 0)
        {
            parts.Add("## Top-K Retrieved Context\n" + string.Join("\n", topK.Select(b => $"- [{b.Layer}] {b.Content}")));
        }

        if (result.HotResults.Count > 0)
            parts.Add("## Working Context\n" + string.Join("\n", result.HotResults.Where(b => !topK.Contains(b)).Select(b => $"- {b.Content}")));
        if (result.DeepResults.Count > 0)
            parts.Add("## Permanent Knowledge\n" + string.Join("\n", result.DeepResults.Where(b => !topK.Contains(b)).Select(b => $"- {b.Content}")));

        if (parts.Count == 0) return userInput;
        return string.Join("\n\n", parts) + "\n\n---\n\n" + userInput;
    }

    public void UpdateHot(string content)
    {
        var block = new MoEMemoryBlock
        {
            Content = content, Layer = ExpertLayer.Hot, SessionId = _sessionId,
            DataValueDensity = ComputeDensity(content)
        };
        _hot.Add(block);
        if (_hot.Count > MaxHot) _hot.RemoveAt(0);
    }

    public int ArchiveToWarm()
    {
        var archived = _hot.Where(b => b.AccessCount >= 2).ToList();
        foreach (var b in archived) { b.Layer = ExpertLayer.Warm; _warm[b.Id] = b; }
        _hot.Clear();
        if (_warm.Count > MaxWarm)
        {
            var remove = _warm.Values.OrderBy(b => b.DecayedRelevance(DateTimeOffset.UtcNow.ToUnixTimeSeconds())).Take(_warm.Count - MaxWarm).ToList();
            foreach (var b in remove) _warm.Remove(b.Id);
        }
        return archived.Count;
    }

    public int Consolidate()
    {
        var moved = 0; var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var b in _warm.Values.Where(b => b.AccessCount >= 5).ToList()) { b.Layer = ExpertLayer.Cold; _cold[b.Id] = b; _warm.Remove(b.Id); moved++; }
        foreach (var b in _cold.Values.OrderByDescending(b => b.DecayedRelevance(t) * b.DataValueDensity).Take(20).ToList())
        { if (b.AccessCount >= 10) { b.Layer = ExpertLayer.Deep; _deep[b.Id] = b; _cold.Remove(b.Id); moved++; } }
        var culled = _cold.Values.Where(b => b.DecayedRelevance(t) < 0.01).ToList();
        foreach (var b in culled) _cold.Remove(b.Id);
        moved += culled.Count;
        if (moved > 0) Save();
        return moved;
    }

    public Dictionary<string, int> AllocateBudget(int totalTokens, int systemTokens)
    {
        var r = totalTokens - systemTokens;
        return new() { ["flash"] = (int)(r * 0.05), ["hot"] = (int)(r * 0.35), ["warm"] = (int)(r * 0.30), ["cold"] = (int)(r * 0.20), ["deep"] = (int)(r * 0.10) };
    }

    public void LinkMemory(string src, string dst)
    {
        if (!_activation.InternalGraph.ContainsKey(src)) _activation.InternalGraph[src] = new HashSet<string>();
        _activation.InternalGraph[src].Add(dst);
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["session_id"] = _sessionId, ["flash"] = _flash.Count, ["hot"] = _hot.Count,
        ["warm"] = _warm.Count, ["cold"] = _cold.Count, ["deep"] = _deep.Count
    };

    private static double Match(MoEMemoryBlock block, string query)
    {
        var bl = block.Content.ToLower(); var ql = query.ToLower();
        if (bl.Contains(ql)) return 0.8;
        var words = ql.Split(' '); return words.Length > 0 ? (double)words.Count(w => bl.Contains(w)) / words.Length * 0.5 : 0;
    }

    private static double ComputeDensity(string content)
    {
        var words = content.Split(' '); if (words.Length == 0) return 0;
        return Math.Min(1.0, (double)new HashSet<string>(words.Select(w => w.ToLower())).Count / words.Length);
    }

    private string StateFile() => global::System.IO.Path.Combine(_dataDir, $"state_{_sessionId}.json");

    private void Save()
    {
        var state = new
        {
            hot = _hot.Select(b => new { b.Id, b.Content, b.Layer, b.Relevance, b.Timestamp, b.LastAccess, b.AccessCount, b.SessionId, b.TaskCriticality }),
            warm = _warm.Values.Select(b => new { b.Id, b.Content, b.Layer, b.Relevance, b.Timestamp, b.LastAccess, b.AccessCount, b.SessionId, b.TaskCriticality }),
            cold = _cold.Values.Select(b => new { b.Id, b.Content, b.Layer, b.Relevance, b.Timestamp, b.LastAccess, b.AccessCount, b.SessionId, b.TaskCriticality }),
            deep = _deep.Values.Select(b => new { b.Id, b.Content, b.Layer, b.Relevance, b.Timestamp, b.LastAccess, b.AccessCount, b.SessionId, b.TaskCriticality })
        };
        global::System.IO.File.WriteAllText(StateFile(), JsonSerializer.Serialize(state));
    }

    private void Load()
    {
        var path = StateFile(); if (!global::System.IO.File.Exists(path)) return;
        try
        {
            var json = global::System.IO.File.ReadAllText(path); var doc = JsonDocument.Parse(json).RootElement;
            LoadBlocks(doc, "hot", _hot, ExpertLayer.Hot);
            LoadDict(doc, "warm", _warm, ExpertLayer.Warm);
            LoadDict(doc, "cold", _cold, ExpertLayer.Cold);
            LoadDict(doc, "deep", _deep, ExpertLayer.Deep);
        }
        catch { /* non-fatal */ }
    }

    private static void LoadBlocks(JsonElement doc, string key, List<MoEMemoryBlock> target, ExpertLayer layer)
    {
        if (!doc.TryGetProperty(key, out var arr)) return;
        foreach (var item in arr.EnumerateArray()) { var b = Parse(item); b.Layer = layer; target.Add(b); }
    }

    private static void LoadDict(JsonElement doc, string key, Dictionary<string, MoEMemoryBlock> target, ExpertLayer layer)
    {
        if (!doc.TryGetProperty(key, out var arr)) return;
        foreach (var item in arr.EnumerateArray()) { var b = Parse(item); b.Layer = layer; target[b.Id] = b; }
    }

    private static MoEMemoryBlock Parse(JsonElement item) => new()
    {
        Id = item.TryGetProperty("id", out var v) ? v.GetString() ?? "" : "",
        Content = item.TryGetProperty("content", out v) ? v.GetString() ?? "" : "",
        Relevance = item.TryGetProperty("relevance", out v) ? v.GetDouble() : 0.5,
        Timestamp = item.TryGetProperty("timestamp", out v) ? v.GetDouble() : 0,
        LastAccess = item.TryGetProperty("lastAccess", out v) ? v.GetDouble() : 0,
        AccessCount = item.TryGetProperty("accessCount", out v) ? v.GetInt32() : 0,
        SessionId = item.TryGetProperty("sessionId", out v) ? v.GetString() ?? "" : "",
        TaskCriticality = item.TryGetProperty("taskCriticality", out v) ? v.GetDouble() : 0
    };

    internal static double[] GetQueryVector(string query, int dim = 64)
    {
        var vec = new double[dim];
        var hash = (uint)query.GetHashCode();
        var rng = new Random((int)hash);
        for (int i = 0; i < dim; i++)
            vec[i] = (rng.NextDouble() - 0.5) * 2.0;
        var norm = Math.Sqrt(vec.Sum(v => v * v));
        if (norm > 1e-8)
            for (int i = 0; i < dim; i++) vec[i] /= norm;
        return vec;
    }

    private static double CosineSimilarity(double[] a, double[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB) + 1e-9);
    }

    private static double ComputeRoutingEntropy(Dictionary<string, double> gates)
    {
        if (gates.Count == 0) return 0;
        double entropy = 0;
        foreach (var v in gates.Values)
        {
            if (v > 1e-9)
                entropy -= v * Math.Log2(v);
        }
        return entropy / Math.Log2(gates.Count);
    }
}

public sealed class MoERouter
{
    private readonly Dictionary<string, Dictionary<ExpertLayer, double>> _taskWeights = new()
    {
        ["code"] = new() { [ExpertLayer.Flash] = 0.1, [ExpertLayer.Hot] = 0.3, [ExpertLayer.Warm] = 0.25, [ExpertLayer.Cold] = 0.2, [ExpertLayer.Deep] = 0.15 },
        ["reasoning"] = new() { [ExpertLayer.Flash] = 0.05, [ExpertLayer.Hot] = 0.25, [ExpertLayer.Warm] = 0.2, [ExpertLayer.Cold] = 0.25, [ExpertLayer.Deep] = 0.25 },
        ["chat"] = new() { [ExpertLayer.Flash] = 0.3, [ExpertLayer.Hot] = 0.4, [ExpertLayer.Warm] = 0.15, [ExpertLayer.Cold] = 0.1, [ExpertLayer.Deep] = 0.05 },
        ["general"] = new() { [ExpertLayer.Flash] = 0.2, [ExpertLayer.Hot] = 0.3, [ExpertLayer.Warm] = 0.25, [ExpertLayer.Cold] = 0.15, [ExpertLayer.Deep] = 0.1 }
    };

    private readonly ConcurrentDictionary<string, int> _taskHits = new();
    private double _learningRate = 0.05;

    public (Dictionary<string, double> weights, Dictionary<string, double> gates) GetWeightsAndGates(
        string taskType, string query, double[] queryVec)
    {
        var baseW = _taskWeights.GetValueOrDefault(taskType, _taskWeights["general"]);
        var raw = baseW.ToDictionary(k => k.Key.ToString().ToLower(), v => v.Value);

        var queryLength = query.Length;
        if (queryLength > 500) { raw["deep"] += 0.15; raw["flash"] -= 0.05; }
        else if (queryLength > 200) { raw["deep"] += 0.1; }
        else if (queryLength < 50) { raw["flash"] += 0.1; }

        double sum = raw.Values.Sum();
        var gates = raw.ToDictionary(k => k.Key, v => v.Value / sum);

        var weights = raw.ToDictionary(k => k.Key, v => Math.Min(1.0, Math.Max(0.05, v.Value)));
        return (weights, gates);
    }

    public Dictionary<string, double> GetWeights(string taskType, string query)
    {
        var (weights, _) = GetWeightsAndGates(taskType, query, ContextMoE.GetQueryVector(query));
        return weights;
    }

    public void RecordRetrieval(string taskType, MoEQueryResult result)
    {
        _taskHits.AddOrUpdate(taskType, 1, (_, v) => v + 1);

        if (!_taskWeights.TryGetValue(taskType, out var weights)) return;

        var activatedLayers = result.All()
            .GroupBy(b => b.Layer)
            .ToDictionary(g => g.Key, g => (double)g.Count());

        var totalActivated = activatedLayers.Values.Sum();
        if (totalActivated <= 0) return;

        double boost = 0;
        foreach (var b in result.HotResults)
            boost += b.RetrievalScore * 0.2;

        foreach (var layer in Enum.GetValues<ExpertLayer>())
        {
            if (!weights.ContainsKey(layer)) continue;
            double activatedFrac = activatedLayers.GetValueOrDefault(layer) / Math.Max(1, totalActivated);
            weights[layer] = weights[layer] * (1.0 - _learningRate) + activatedFrac * _learningRate + boost * _learningRate * 0.1;
            weights[layer] = Math.Min(1.0, Math.Max(0.05, weights[layer]));
        }
    }

    public void AdaptToQuery(string query, Dictionary<string, double> resultWeights)
    {
        foreach (var (layer, weight) in resultWeights)
        {
            foreach (var taskWeights in _taskWeights.Values)
            {
                if (taskWeights.TryGetValue(Enum.Parse<ExpertLayer>(layer, true), out var current))
                    taskWeights[Enum.Parse<ExpertLayer>(layer, true)] = current * 0.99 + weight * 0.01;
            }
        }
    }
}

public sealed class SpreadingActivation
{
    public Dictionary<string, HashSet<string>> InternalGraph { get; } = new();
    private readonly Dictionary<string, double> _activationScores = new();

    public List<MoEMemoryBlock> Spread(string query, List<MoEMemoryBlock> seeds)
    {
        var activated = new HashSet<string>();
        var result = new List<MoEMemoryBlock>();

        foreach (var seed in seeds)
        {
            if (activated.Add(seed.Id))
            {
                seed.RetrievalScore *= 1.1;
                result.Add(seed);
            }
        }

        var decay = 0.5;
        foreach (var seed in seeds)
        {
            if (!InternalGraph.TryGetValue(seed.Id, out var neighbors)) continue;

            foreach (var neighborId in neighbors)
            {
                if (!activated.Add(neighborId)) continue;

                var neighborBlock = seeds.Concat(result)
                    .FirstOrDefault(b => b.Id == neighborId);
                if (neighborBlock != null)
                {
                    neighborBlock.RetrievalScore *= (1.0 + decay * 0.2);
                    result.Add(neighborBlock);
                }
            }

            decay *= 0.5;
        }

        return result;
    }

    public double GetActivationBoost(string blockId, string query)
    {
        if (_activationScores.TryGetValue(blockId, out var score))
            return score;

        if (!InternalGraph.TryGetValue(blockId, out var neighbors)) return 0;
        var boost = neighbors.Count * 0.05;
        _activationScores[blockId] = boost;
        return boost;
    }

    public void LinkMemory(string src, string dst)
    {
        if (!InternalGraph.ContainsKey(src))
            InternalGraph[src] = new HashSet<string>();
        InternalGraph[src].Add(dst);
        _activationScores.Clear();
    }
}
