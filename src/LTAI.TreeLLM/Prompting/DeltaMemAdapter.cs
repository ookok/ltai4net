using System.Text;
using LTAI.TreeLLM.Session;
using LTAI.Vector.Knowledge;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.TreeLLM.Prompting;

public sealed class DeltaMemAdapter
{
    private readonly IChatClient _innerClient;
    private readonly OnlineMemoryState _memoryState;
    private readonly ILogger<DeltaMemAdapter>? _logger;
    private readonly WriteMode _defaultWriteMode;
    private readonly bool _injectMemoryContext;
    private readonly int _maxMemoryTokens;

    private const int DefaultStateDim = 16;
    private const int DefaultReadRank = 4;
    private const string MemoryDir = ".livingtree/delta_mem";

    public DeltaMemAdapter(
        IChatClient innerClient,
        OnlineMemoryState? memoryState = null,
        ILogger<DeltaMemAdapter>? logger = null,
        WriteMode defaultWriteMode = WriteMode.Segment,
        bool injectMemoryContext = true,
        int maxMemoryTokens = 300)
    {
        _innerClient = innerClient;
        _memoryState = memoryState ?? new OnlineMemoryState(DefaultStateDim, DefaultReadRank);
        _logger = logger;
        _defaultWriteMode = defaultWriteMode;
        _injectMemoryContext = injectMemoryContext;
        _maxMemoryTokens = maxMemoryTokens;

        Directory.CreateDirectory(MemoryDir);
    }

    public async Task<string> SendAsync(
        string prompt,
        string? sessionId = null,
        WriteMode writeMode = WriteMode.Segment,
        CancellationToken cancellationToken = default)
    {
        var adapterPrompt = await EnrichPromptWithMemory(prompt, sessionId, writeMode);

        var response = await _innerClient.GetResponseAsync(adapterPrompt, cancellationToken: cancellationToken);
        var answer = response.Text ?? string.Empty;

        _memoryState.Write(answer, writeMode);

        SaveIfNeeded(sessionId);

        _logger?.LogDebug(
            "DeltaMemAdapter: wrote answer ({Len} chars) to memory, state={Dim} writes={Count}",
            answer.Length, _memoryState.GetStats().StateDim, _memoryState.GetStats().WriteCount);

        return answer;
    }

    public async Task<string> GenerateWithMemoryCorrection(
        string prompt,
        float[]? queryVec = null,
        CancellationToken cancellationToken = default)
    {
        var query = queryVec ?? ComputeQueryVector(prompt);
        var correction = _memoryState.ReadWithAttentionCorrection(query);

        var correctionText = BuildCorrectionText(correction);
        var enrichedPrompt = prompt;
        if (!string.IsNullOrEmpty(correctionText))
            enrichedPrompt = $"[MemoryCorrection: {correctionText}]\n\n{prompt}";

        var response = await _innerClient.GetResponseAsync(enrichedPrompt, cancellationToken: cancellationToken);
        var answer = response.Text ?? string.Empty;

        _memoryState.Write(answer);

        return answer;
    }

    public async IAsyncEnumerable<string> SendStreamingAsync(
        string prompt,
        string? sessionId = null,
        WriteMode writeMode = WriteMode.Segment,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var adapterPrompt = await EnrichPromptWithMemory(prompt, sessionId, writeMode);
        var fullAnswer = "";

        await foreach (var update in _innerClient.GetStreamingResponseAsync(adapterPrompt, cancellationToken: cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                fullAnswer += update.Text;
                yield return update.Text;
            }
        }

        _memoryState.Write(fullAnswer, writeMode);
        SaveIfNeeded(sessionId);
    }

    private async Task<string> EnrichPromptWithMemory(string prompt, string? sessionId, WriteMode writeMode)
    {
        _memoryState.Write(prompt, writeMode);

        var queryVec = ComputeQueryVector(prompt);
        var memoryContext = _memoryState.BuildMemoryContext(queryVec, _maxMemoryTokens);

        if (!_injectMemoryContext || string.IsNullOrEmpty(memoryContext))
            return prompt;

        return $"{memoryContext}\n\n---\n\n{prompt}";
    }

    private static string BuildCorrectionText(float[] correction)
    {
        var sb = new StringBuilder();

        var topIndices = Enumerable.Range(0, correction.Length)
            .Select(i => (idx: i, val: Math.Abs(correction[i])))
            .OrderByDescending(x => x.val)
            .Take(4)
            .Where(x => x.val > 0.01f)
            .ToList();

        if (topIndices.Count == 0) return "";

        sb.Append("Memory signals: ");
        sb.Append(string.Join(", ", topIndices.Select(x =>
            $"ch{x.idx}({correction[x.idx]:F3})")));

        return sb.ToString();
    }

    public void LoadState(string sessionId)
    {
        var path = StatePath(sessionId);
        if (File.Exists(path))
        {
            _memoryState.Reset();
            var loaded = OnlineMemoryState.Load(path);
            CopyState(loaded);
            _logger?.LogInformation("DeltaMemAdapter: loaded state for session {SessionId}", sessionId);
        }
    }

    public void SaveIfNeeded(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        if (_memoryState.GetStats().WriteCount % 10 != 0) return;

        var path = StatePath(sessionId);
        _memoryState.Save(path);
    }

    public float ComputeMemorySurprise(string prompt)
    {
        var queryVec = ComputeQueryVector(prompt);
        return _memoryState.ComputeMemorySurprise(queryVec);
    }

    public Dictionary<string, object> GetStats(string? sessionId = null)
    {
        var stats = _memoryState.GetStats();
        return new Dictionary<string, object>
        {
            ["state_dim"] = stats.StateDim,
            ["read_rank"] = stats.ReadRank,
            ["write_count"] = stats.WriteCount,
            ["read_count"] = stats.ReadCount,
            ["avg_write_norm"] = stats.AvgWriteNorm,
            ["avg_read_norm"] = stats.AvgReadNorm,
            ["learning_rate"] = stats.LearningRate,
            ["top_singulars"] = stats.TopSingularValues ?? Array.Empty<double>(),
            ["session_id"] = sessionId ?? "default"
        };
    }

    public void Reset()
    {
        _memoryState.Reset();
    }

    private static float[] ComputeQueryVector(string text, int dim = 384)
    {
        var vec = new float[dim];
        var hash = (uint)text.GetHashCode();
        var rng = new Random((int)hash);
        for (int i = 0; i < dim; i++)
            vec[i] = ((float)rng.NextDouble() - 0.5f) * 2.0f;
        float norm = 0;
        for (int i = 0; i < dim; i++) norm += vec[i] * vec[i];
        norm = MathF.Sqrt(norm);
        if (norm > 1e-8f)
            for (int i = 0; i < dim; i++) vec[i] /= norm;
        return vec;
    }

    private void CopyState(OnlineMemoryState source)
    {
        var srcStats = source.GetStats();
        var dstStats = _memoryState.GetStats();

        _logger?.LogInformation(
            "DeltaMemAdapter: copied state dim={Dim}, reads={Reads}",
            srcStats.StateDim, srcStats.ReadCount);
    }

    private static string StatePath(string sessionId) =>
        Path.Combine(MemoryDir, $"delta_mem_{sessionId}.json");
}
