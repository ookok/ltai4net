using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.Knowledge.Vector.Interfaces;
using LTAI.Tools.Tools;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

public sealed class ToolDefResult
{
    public ToolDef Definition { get; init; } = null!;
    public float Score { get; init; }
}

public sealed class ToolRetriever
{
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<ToolRetriever> _logger;
    private readonly Dictionary<string, (ToolDef Tool, float[] Embedding)> _toolIndex = new();
    private readonly ConcurrentDictionary<string, (int Successes, int Failures)> _feedback = new();
    private static readonly string FeedbackPath = Path.Combine(
        OptionService.Get("LTAI_WORKSPACE") ?? Environment.CurrentDirectory,
        OptionService.Get("paths.DataDirectory") ?? ".livingtree", "meta", "tool_feedback.json");
    private bool _initialized;

    private static readonly string[] CoreTools =
    {
        "vfs:read", "vfs:write", "vfs:list", "shell:exec", "http:get"
    };

    public ToolRetriever(IVectorStore vectorStore, ILogger<ToolRetriever> logger)
    {
        _vectorStore = vectorStore;
        _logger = logger;
    }

    public async Task IndexAllToolsAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        var tools = LTAIToolRegistry.AllTools.Where(t => t.Handler != null).ToList();
        var descriptions = tools.Select(t => $"{t.Name}: {t.Description}").ToList();

        // Batch-embed all tool descriptions in a single backend call
        var embeddings = await _vectorStore.EmbedBatchAsync(descriptions, ct).ConfigureAwait(false);

        for (int i = 0; i < tools.Count && i < embeddings.Length; i++)
        {
            try
            {
                _toolIndex[tools[i].Name] = (tools[i], embeddings[i]);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ToolRetriever: failed to index {Tool}", tools[i].Name);
            }
        }

        LoadFeedback();
        _initialized = true;
        _logger.LogInformation("ToolRetriever: indexed {Count} tools, {FbCount} feedback entries", _toolIndex.Count, _feedback.Count);
    }

    public async Task<IReadOnlyList<ToolDefResult>> RetrieveToolsAsync(
        string intent, string query, int topK = 12, CancellationToken ct = default)
    {
        if (!_initialized)
            return CoreTools.Select(n => new ToolDefResult
            {
                Definition = new ToolDef(n, n, "core", null), Score = 0.5f
            }).ToList();

        try
        {
            var queryText = $"{intent}: {query}";
            var queryEmbedding = await _vectorStore.EmbedAsync(queryText, ct).ConfigureAwait(false);

            var scored = _toolIndex.Values
                .Select(kv =>
                {
                    var baseScore = CosineSimilarity(queryEmbedding, kv.Embedding);
                    var fb = _feedback.GetValueOrDefault(kv.Tool.Name);
                    var fbBonus = fb.Successes > 0 || fb.Failures > 0
                        ? ((float)(fb.Successes + 1) / (fb.Successes + fb.Failures + 2) - 0.5f) * 0.3f
                        : 0f;
                    return new ToolDefResult
                    {
                        Definition = kv.Tool,
                        Score = Math.Clamp(baseScore + fbBonus, 0, 1)
                    };
                })
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .ToList();

            foreach (var core in CoreTools)
            {
                if (_toolIndex.TryGetValue(core, out var t) &&
                    !scored.Any(s => s.Definition.Name == core))
                    scored.Add(new ToolDefResult { Definition = t.Tool, Score = 0.5f });
            }

            return scored;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ToolRetriever: retrieval failed, falling back to core tools");
            return CoreTools.Select(n => new ToolDefResult
            {
                Definition = new ToolDef(n, n, "core", null), Score = 0.2f
            }).ToList();
        }
    }

    /// <summary>
    /// Record feedback: successful tool invocation → boost future ranking.
    /// </summary>
    public void RecordFeedback(string toolName, bool success)
    {
        _feedback.AddOrUpdate(toolName,
            _ => success ? (1, 0) : (0, 1),
            (_, v) => success ? (v.Successes + 1, v.Failures) : (v.Successes, v.Failures + 1));
        SaveFeedback();
    }

    private void LoadFeedback()
    {
        try
        {
            if (!File.Exists(FeedbackPath)) return;
            var json = File.ReadAllText(FeedbackPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, int[]>>(json);
            if (data == null) return;
            foreach (var kv in data)
                _feedback[kv.Key] = (kv.Value[0], kv.Value[1]);
        }
        catch { /* best-effort: start fresh */ }
    }

    private void SaveFeedback()
    {
        try
        {
            var data = _feedback.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.Successes, kv.Value.Failures });
            // Use AsyncDisk for non-blocking batched write instead of synchronous File.WriteAllText
            LTAI.Core.System.AsyncDisk.Instance.WriteJson(FeedbackPath, data);
        }
        catch { /* best-effort: feedback loss acceptable */ }
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return normA > 0 && normB > 0 ? (float)(dot / (Math.Sqrt(normA) * Math.Sqrt(normB))) : 0;
    }
}
