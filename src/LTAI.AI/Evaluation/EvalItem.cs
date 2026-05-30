// Copyright (c) LTAI. All rights reserved.

using System;
using System.Collections.Generic;

namespace LTAI.AI.Evaluation;

/// <summary>
/// Represents a single evaluation unit: a user query, the agent's response,
/// and optional context/expectations for verification.
/// </summary>
public sealed class EvalItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EvalItem"/> class.
    /// </summary>
    /// <param name="query">The user query or input.</param>
    /// <param name="response">The agent's response.</param>
    /// <param name="context">Optional context or ground truth for reference-based checks.</param>
    /// <param name="name">Optional human-readable name for this item.</param>
    public EvalItem(string query, string response, string? context = null, string? name = null)
    {
        this.Query = query ?? throw new ArgumentNullException(nameof(query));
        this.Response = response ?? throw new ArgumentNullException(nameof(response));
        this.Context = context;
        this.Name = name ?? $"Item-{DateTime.UtcNow.Ticks % 10000}";
    }

    /// <summary>User query or input.</summary>
    public string Query { get; }

    /// <summary>Agent's response text.</summary>
    public string Response { get; }

    /// <summary>Optional context / ground truth for reference-based checks.</summary>
    public string? Context { get; }

    /// <summary>Human-readable name for this item.</summary>
    public string Name { get; }

    /// <summary>Optional tags for categorizing evaluation items.</summary>
    public HashSet<string> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional expected tool call names (for tool-call verification).</summary>
    public List<string> ExpectedToolCalls { get; set; } = [];

    /// <summary>Custom metadata dictionary for extensibility.</summary>
    public Dictionary<string, object?> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the item name for display.</summary>
    public override string ToString() => $"{this.Name}: \"{Truncate(this.Query, 60)}\"";

    private static string Truncate(string s, int maxLength) =>
        s.Length <= maxLength ? s : s[..maxLength] + "...";
}
