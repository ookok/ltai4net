using System.Diagnostics;

namespace LTAI.Core.Telemetry;

/// <summary>
/// Shared ActivitySource for LTAI telemetry. Exports via OpenTelemetry
/// when configured, otherwise no-ops. Covers: request flow, tool calls,
/// LLM invocations, and memory operations.
/// </summary>
public static class LTAIDiagnostics
{
    /// <summary>ActivitySource name for OpenTelemetry configuration.</summary>
    public const string SourceName = "LTAI";

    /// <summary>Version for telemetry metadata.</summary>
    public const string SourceVersion = "1.0.0";

    private static readonly ActivitySource _source = new(SourceName, SourceVersion);

    /// <summary>Start a root activity for an Agent request.</summary>
    public static Activity? StartRequest(string operation, string query)
    {
        var activity = _source.StartActivity(operation, ActivityKind.Server);
        activity?.SetTag("ltai.query.length", query.Length);
        activity?.SetTag("ltai.query.preview", query.Length > 100 ? query[..100] + "..." : query);
        return activity;
    }

    /// <summary>Start an activity for a tool invocation.</summary>
    public static Activity? StartToolCall(string toolName)
    {
        var activity = _source.StartActivity($"tool:{toolName}", ActivityKind.Internal);
        activity?.SetTag("ltai.tool", toolName);
        return activity;
    }

    /// <summary>Start an activity for an LLM call.</summary>
    public static Activity? StartLlmCall(string model, string label)
    {
        var activity = _source.StartActivity($"llm:{label}", ActivityKind.Internal);
        activity?.SetTag("ltai.model", model);
        activity?.SetTag("ltai.label", label);
        return activity;
    }

    /// <summary>Start an activity for a memory/vector operation.</summary>
    public static Activity? StartMemoryOp(string operation)
    {
        return _source.StartActivity($"memory:{operation}", ActivityKind.Internal);
    }

    /// <summary>Record an exception on an activity and stop it.</summary>
    public static void SetError(this Activity? activity, string message)
    {
        if (activity == null) return;
        activity.SetTag("error", true);
        activity.SetTag("error.message", message);
    }
}
