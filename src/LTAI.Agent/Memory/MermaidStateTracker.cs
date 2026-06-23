// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  MermaidStateTracker — builds Mermaid stateDiagram-v2 from
//  tool execution flow. Inspired by TencentDB-Agent-Memory:
//  symbolic state diagrams + node_id references replace verbose
//  execution traces in context, saving ~61% tokens.
// ═══════════════════════════════════════════════════════════════

using System.Text;

namespace LTAI.Agent.Memory;

/// <summary>A single transition between tool states.</summary>
public sealed record MermaidTransition(
    string FromState,
    string ToState,
    string Label,
    string? RefId);

/// <summary>
/// Tracks tool execution as a lightweight Mermaid state diagram.
/// Each tool call becomes a state transition; heavy results are
/// referenced via <c>node_id</c> (refs file paths).
/// </summary>
public sealed class MermaidStateTracker
{
    private readonly List<MermaidTransition> _transitions = [];
    private readonly HashSet<string> _states = new(StringComparer.OrdinalIgnoreCase);
    private string _currentState = "init";

    /// <summary>Current state label.</summary>
    public string CurrentState => _currentState;

    /// <summary>Record a state transition from a tool execution.</summary>
    public void RecordTransition(
        string fromState, string toState, string label, string? refId = null)
    {
        _transitions.Add(new MermaidTransition(fromState, toState, label, refId));
        _states.Add(fromState);
        _states.Add(toState);
        _currentState = toState;
        InvalidateCache();
    }

    /// <summary>
    /// Record a tool call as a state transition.
    /// Tool results that were offloaded have a refId; inline results are null.
    /// </summary>
    public void RecordToolCall(
        string toolName, string? refId, bool isSuccessful)
    {
        var label = isSuccessful ? $"{toolName} ✔" : $"{toolName} ✘";
        var fromState = _currentState;
        var toState = SanitizeStateName(toolName);
        RecordTransition(fromState, toState, label, refId);
    }

    /// <summary>
    /// Record a user message as a state transition (new request).
    /// </summary>
    public void RecordUserMessage(string summary)
    {
        var label = summary.Length > 40 ? summary[..40] + "…" : summary;
        var fromState = _currentState;
        var toState = "user-input";
        RecordTransition(fromState, toState, label, null);
    }

    /// <summary>Builds the <c>stateDiagram-v2</c> Mermaid markup.</summary>
    public string BuildDiagram()
    {
        if (_transitions.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("```mermaid");
        sb.AppendLine("stateDiagram-v2");

        var stateStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _transitions)
        {
            var fromId = MakeNodeId(t.FromState);
            var toId = MakeNodeId(t.ToState);

            if (t.RefId != null)
            {
                sb.AppendLine($"    state \"{t.Label}\" as {toId}: click {toId} \"refs/{t.RefId}\" \"View details\"");
            }
            else
            {
                sb.AppendLine($"    state \"{t.Label}\" as {toId}");
            }

            stateStyles.Add(t.FromState);
            stateStyles.Add(t.ToState);
        }

        foreach (var t in _transitions)
        {
            var fromId = MakeNodeId(t.FromState);
            var toId = MakeNodeId(t.ToState);
            sb.AppendLine($"    {fromId} --> {toId}");
        }

        sb.AppendLine("```");
        return sb.ToString();
    }

    /// <summary>
    /// Builds a compact one-line state summary for inline use.
    /// Example: <c>init → SearchContent → ReadFile → Respond</c>
    /// </summary>
    public string BuildCompactSummary()
    {
        if (_transitions.Count == 0) return "init";
        var parts = new List<string> { "init" };
        foreach (var t in _transitions)
        {
            var label = t.Label.Length > 20 ? t.Label[..20] + "…" : t.Label;
            parts.Add(label);
        }
        return string.Join(" → ", parts);
    }

    /// <summary>Number of transitions recorded.</summary>
    public int TransitionCount => _transitions.Count;

    /// <summary>The raw Mermaid diagram text if already built (for incremental updates).</summary>
    private string? _cachedDiagram;
    private string? _cachedSummary;

    /// <summary>Reset the tracker for a new execution flow.</summary>
    public void Reset()
    {
        _transitions.Clear();
        _states.Clear();
        _currentState = "init";
        _cachedDiagram = null;
        _cachedSummary = null;
    }

    /// <summary>
    /// Append a <c>note</c> node to the existing diagram without full rebuild.
    /// Avoids re-serializing all previous transitions.
    /// </summary>
    public string AppendNote(string noteText)
    {
        if (string.IsNullOrEmpty(noteText)) return _cachedDiagram ?? BuildDiagram();
        var sanitized = SanitizePreservedLabel(noteText);
        var noteId = $"note_{_transitions.Count}";

        if (_cachedDiagram == null)
        {
            _cachedDiagram = BuildDiagram();
            _cachedSummary = BuildCompactSummary();
        }

        // Append note after the closing ``` fence
        var insertPos = _cachedDiagram.LastIndexOf("\n```", StringComparison.Ordinal);
        if (insertPos >= 0)
        {
            var noteLine = $"\n    note \"{sanitized}\" as {noteId}";
            _cachedDiagram = _cachedDiagram.Insert(insertPos, noteLine);
        }
        return _cachedDiagram;
    }

    /// <summary>
    /// Get or build the diagram, using cache if available.
    /// </summary>
    public string GetOrBuildDiagram()
    {
        if (_cachedDiagram != null) return _cachedDiagram;
        _cachedDiagram = BuildDiagram();
        _cachedSummary = BuildCompactSummary();
        return _cachedDiagram;
    }

    /// <summary>
    /// Invalidate cached diagram (after major changes).
    /// </summary>
    public void InvalidateCache()
    {
        _cachedDiagram = null;
        _cachedSummary = null;
    }

    /// <summary>
    /// Build a state diagram from a message flow, offloading verbose messages to refs.
    /// Returns (diagram string, compact summary string, list of offload refs).
    /// </summary>
    public async Task<(string Diagram, string Summary, List<string> Refs)> BuildFromMessageFlowAsync(
        List<Microsoft.Extensions.AI.ChatMessage> messages,
        string traceId,
        ContextOffloader? offloader = null)
    {
        Reset();
        var refs = new List<string>();

        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            var label = GetMessageLabel(msg, i);
            var role = msg.Role.ToString().ToLowerInvariant();

            if (role == "user")
            {
                RecordUserMessage(label);
            }
            else if (role == "assistant" || role == "tool")
            {
                string? refId = null;
                if (offloader != null && !string.IsNullOrEmpty(msg.Text) && ShouldOffload(msg.Text))
                {
                    var seq = i + 1;
                    var offloaded = await offloader.OffloadMessageTextAsync(
                        msg.Text, traceId, SanitizePreservedLabel(label), seq).ConfigureAwait(false);
                    if (offloaded != msg.Text)
                    {
                        refId = offloaded.Replace("[refs/", "").Replace("]", "");
                        refs.Add(refId);
                    }
                }
                RecordToolCall(label, refId, isSuccessful: true);
            }
            else if (role == "system")
            {
                // System messages are reference tokens — always visible, no transition
                continue;
            }
        }

        return (BuildDiagram(), BuildCompactSummary(), refs);
    }

    private static string SanitizePreservedLabel(string label)
    {
        var sb = new StringBuilder(label.Length);
        foreach (var c in label)
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ')
                sb.Append(c);
        return sb.ToString().Trim();
    }

    private static string GetMessageLabel(Microsoft.Extensions.AI.ChatMessage msg, int index)
    {
        var role = msg.Role.ToString();
        if (!string.IsNullOrEmpty(msg.AuthorName))
            return $"{role}:{msg.AuthorName}";
        var text = msg.Text ?? "";
        if (text.Length > 40) text = text[..40] + "…";
        return $"{role}#{index + 1}:{text}";
    }

    private static bool ShouldOffload(string text)
    {
        return text.Length > 2048 || text.Count(c => c == '\n') > 40;
    }

    #region Helpers

    private static string MakeNodeId(string state)
    {
        var sb = new StringBuilder(state.Length);
        foreach (var c in state)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else sb.Append('_');
        }
        var id = sb.ToString().Trim('_');
        return id.Length > 0 ? id : "state";
    }

    private static string SanitizeStateName(string name)
    {
        var parts = name.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts);
    }

    #endregion
}
