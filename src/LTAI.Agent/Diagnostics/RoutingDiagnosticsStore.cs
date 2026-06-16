// Copyright (c) LTAI. All rights reserved.

using System.Text.Json;
using LTAI.Agent.Workflows;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Diagnostics;

/// <summary>
/// P0: Persists routing decisions to a JSON Lines file for post-hoc analysis.
/// Each line in <c>.livingtree/diagnostics/routing.jsonl</c> is a JSON object
/// with timestamp, task preview, branch, scores, candidates, and embed tier.
/// </summary>
public sealed class RoutingDiagnosticsStore : IDisposable
{
    private readonly string _filePath;
    private readonly ILogger<RoutingDiagnosticsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StreamWriter? _writer;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public RoutingDiagnosticsStore(
        string dataDir,
        ILogger<RoutingDiagnosticsStore> logger)
    {
        _logger = logger;
        var dir = Path.Combine(dataDir, "diagnostics");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "routing.jsonl");
    }

    /// <summary>Record a routing decision as a JSON line.</summary>
    public async Task RecordAsync(
        string task,
        DecisionTreeResult routing,
        IReadOnlyList<string>? agentNames = null,
        CancellationToken ct = default)
    {
        var entry = new
        {
            Timestamp = DateTime.UtcNow.ToString("o"),
            TaskPreview = (task?.Length ?? 0) > 200 ? task![..197] + "..." : task,
            routing.Branch,
            TopScore = Math.Round(routing.TopScore, 4),
            Margin = Math.Round(routing.Margin, 4),
            Candidates = (agentNames ?? routing.Candidates)?.ToArray() ?? [],
            EmbeddingTier = routing.EmbeddingTier.ToString(),
        };

        var json = JsonSerializer.Serialize(entry, JsonOpts);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _writer ??= new StreamWriter(_filePath, append: true, encoding: System.Text.Encoding.UTF8);
            await _writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write routing diagnostic");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        // Don't block if another thread holds the gate — just skip the write flush
        if (_gate.Wait(0))
        {
            try { _writer?.Dispose(); } catch
            {
                // non-critical, best-effort
            }
            _writer = null;
            _gate.Release();
        }
        _gate.Dispose();
    }
}
