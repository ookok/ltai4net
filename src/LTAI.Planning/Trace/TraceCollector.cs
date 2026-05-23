using System.Collections.Concurrent;
using System.Text.Json;

namespace LTAI.Planning.Trace;

public enum TraceStepType { IntentRouting, AgentSelection, ToolCall, KnowledgeRetrieval, ModelCall, Verification, OutputFinal }

public sealed record TraceStep
{
    public int Sequence { get; init; }
    public string TraceId { get; init; } = "";
    public string SessionId { get; init; } = "";
    public TraceStepType Type { get; init; }
    public string AgentName { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Input { get; init; }
    public string? Output { get; init; }
    public string? Reasoning { get; init; }
    public string? DataSource { get; init; }
    public string? StandardReference { get; init; }
    public double Confidence { get; init; }
    public long LatencyMs { get; init; }
    public bool Success { get; init; } = true;
    public string? ErrorMessage { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed class ExplainabilityTrace
{
    public string TraceId { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string UserQuery { get; init; } = "";
    public string FinalResponse { get; set; } = "";
    public double OverallConfidence { get; set; }
    public List<TraceStep> Steps { get; init; } = new();
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime CompletedAt { get; set; }
    public int TotalTokens { get; set; }
    public string Verdict { get; set; } = "UNKNOWN";
}

public sealed class TraceCollector
{
    private readonly ConcurrentDictionary<string, ExplainabilityTrace> _traces = new();
    private readonly ConcurrentDictionary<string, int> _sequenceCounters = new();
    private readonly object _lock = new();
    private const int MaxTraces = 500;

    public ExplainabilityTrace StartTrace(string sessionId, string userQuery)
    {
        var traceId = Guid.NewGuid().ToString("N")[..16];
        var trace = new ExplainabilityTrace
        {
            TraceId = traceId,
            SessionId = sessionId,
            UserQuery = userQuery
        };

        _traces[traceId] = trace;

        lock (_lock)
        {
            if (_traces.Count > MaxTraces)
            {
                var oldest = _traces.Values
                    .OrderBy(t => t.StartedAt)
                    .Take(50)
                    .Select(t => t.TraceId)
                    .ToList();
                foreach (var id in oldest) _traces.TryRemove(id, out _);
            }
        }

        return trace;
    }

    public void AddStep(string traceId, TraceStep step)
    {
        if (!_traces.TryGetValue(traceId, out var trace))
            return;

        var seq = _sequenceCounters.AddOrUpdate(traceId, 1, (_, v) => v + 1);
        step = step with { Sequence = seq };

        lock (trace)
        {
            trace.Steps.Add(step);
        }
    }

    public void RecordIntentRouting(string traceId, string agentName, string intent,
        float confidence, string matchedKeywords, string queryShape)
    {
        AddStep(traceId, new TraceStep
        {
            Type = TraceStepType.IntentRouting,
            AgentName = agentName,
            Description = $"Intent classified as '{intent}'",
            Reasoning = $"Matched keywords: {matchedKeywords}",
            Confidence = confidence,
            Metadata = new() { ["intent"] = intent, ["query_shape"] = queryShape }
        });
    }

    public void RecordToolCall(string traceId, string agentName, string toolName,
        string input, string output, long latencyMs, bool success, string? error = null)
    {
        AddStep(traceId, new TraceStep
        {
            Type = TraceStepType.ToolCall,
            AgentName = agentName,
            Description = $"Tool: {toolName}",
            Input = input?[..Math.Min(input?.Length ?? 0, 200)],
            Output = output?[..Math.Min(output?.Length ?? 0, 200)],
            LatencyMs = latencyMs,
            Success = success,
            ErrorMessage = error,
            Metadata = new() { ["tool_name"] = toolName }
        });
    }

    public void RecordKnowledgeRetrieval(string traceId, string agentName, string query,
        string source, int resultCount, long latencyMs)
    {
        AddStep(traceId, new TraceStep
        {
            Type = TraceStepType.KnowledgeRetrieval,
            AgentName = agentName,
            Description = $"Knowledge search: '{query?[..Math.Min(query?.Length ?? 0, 100)]}'",
            DataSource = source,
            LatencyMs = latencyMs,
            Metadata = new() { ["source"] = source, ["result_count"] = resultCount.ToString() }
        });
    }

    public void RecordModelCall(string traceId, string agentName, string modelUsed,
        int inputTokens, int outputTokens, long latencyMs)
    {
        AddStep(traceId, new TraceStep
        {
            Type = TraceStepType.ModelCall,
            AgentName = agentName,
            Description = $"Model call: {modelUsed}",
            LatencyMs = latencyMs,
            Metadata = new()
            {
                ["model"] = modelUsed,
                ["input_tokens"] = inputTokens.ToString(),
                ["output_tokens"] = outputTokens.ToString()
            }
        });
    }

    public void RecordVerification(string traceId, string agentName, string method,
        string? standardRef, double confidence, bool passed)
    {
        AddStep(traceId, new TraceStep
        {
            Type = TraceStepType.Verification,
            AgentName = agentName,
            Description = $"Verification: {method}",
            StandardReference = standardRef,
            Confidence = confidence,
            Success = passed,
            Metadata = new() { ["method"] = method }
        });
    }

    public void CompleteTrace(string traceId, string finalResponse, double overallConfidence,
        string verdict, int totalTokens)
    {
        if (!_traces.TryGetValue(traceId, out var trace))
            return;

        lock (trace)
        {
            trace.FinalResponse = finalResponse[..Math.Min(finalResponse.Length, 1000)];
            trace.OverallConfidence = overallConfidence;
            trace.Verdict = verdict;
            trace.TotalTokens = totalTokens;
            trace.CompletedAt = DateTime.UtcNow;
        }
    }

    public ExplainabilityTrace? GetTrace(string traceId)
    {
        return _traces.TryGetValue(traceId, out var trace) ? trace : null;
    }

    public List<ExplainabilityTrace> GetRecentTraces(int count = 20)
    {
        return _traces.Values
            .OrderByDescending(t => t.StartedAt)
            .Take(count)
            .ToList();
    }

    public List<ExplainabilityTrace> GetTracesBySession(string sessionId, int count = 10)
    {
        return _traces.Values
            .Where(t => t.SessionId == sessionId)
            .OrderByDescending(t => t.StartedAt)
            .Take(count)
            .ToList();
    }

    public string BuildDecisionTree(string traceId)
    {
        var trace = GetTrace(traceId);
        if (trace == null) return "Trace not found";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Decision Trace: {trace.TraceId}");
        sb.AppendLine($"Query: {trace.UserQuery[..Math.Min(trace.UserQuery.Length, 200)]}");
        sb.AppendLine($"Verdict: {trace.Verdict} | Confidence: {trace.OverallConfidence:F2} | Tokens: {trace.TotalTokens}");
        sb.AppendLine();

        foreach (var step in trace.Steps.OrderBy(s => s.Sequence))
        {
            var emoji = step.Type switch
            {
                TraceStepType.IntentRouting => "🧭",
                TraceStepType.ToolCall => "🔧",
                TraceStepType.KnowledgeRetrieval => "📚",
                TraceStepType.ModelCall => "🤖",
                TraceStepType.Verification => "✅",
                _ => "➡️"
            };

            sb.AppendLine($"{step.Sequence}. {emoji} [{step.Type}] {step.Description}");
            if (step.Reasoning != null)
                sb.AppendLine($"   Reason: {step.Reasoning[..Math.Min(step.Reasoning.Length, 150)]}");
            if (step.DataSource != null)
                sb.AppendLine($"   Source: {step.DataSource}");
            if (step.StandardReference != null)
                sb.AppendLine($"   Standard: {step.StandardReference}");
            if (step.Confidence > 0)
                sb.AppendLine($"   Confidence: {step.Confidence:F2}");
            if (step.LatencyMs > 0)
                sb.AppendLine($"   Latency: {step.LatencyMs}ms");
            if (!step.Success)
                sb.AppendLine($"   ❌ Error: {step.ErrorMessage}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public Dictionary<string, object> GetStats()
    {
        return new()
        {
            ["total_traces"] = _traces.Count,
            ["recent_verdicts"] = _traces.Values
                .OrderByDescending(t => t.StartedAt)
                .Take(10)
                .Select(t => new
                {
                    t.TraceId,
                    t.Verdict,
                    t.OverallConfidence,
                    steps = t.Steps.Count,
                    t.TotalTokens,
                    duration_ms = (t.CompletedAt - t.StartedAt).TotalMilliseconds
                }).ToList()
        };
    }
}
