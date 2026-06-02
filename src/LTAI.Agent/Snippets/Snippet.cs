// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  Snippet — A user-defined "common phrase" / quick-prompt
//
//  Stored in .livingtree/snippets.json. Shared between LTAI.TUI
//  and LTAI.Desktop. Key is a short identifier (no spaces).
//
//  D60  key is the primary lookup; aliases are not currently
//       supported (D60 decision: aliases add bookkeeping cost
//       without clear benefit; users can rename via /snippet rename).
//  D61  UseContent fills the input field — never sends directly.
// ═══════════════════════════════════════════════════════════════

using System.Text.Json.Serialization;

namespace LTAI.Agent.Snippets;

public sealed class Snippet
{
    /// <summary>Unique identifier. Lower-case, no spaces. Max 64 chars.</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    /// <summary>The text content the snippet expands to.</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    /// <summary>Optional human-readable description (shown in /snippet list).</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>When the snippet was first created (UTC).</summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the snippet was last expanded via /snippet use (UTC).</summary>
    [JsonPropertyName("lastUsedAt")]
    public DateTime? LastUsedAt { get; set; }

    /// <summary>How many times this snippet has been expanded.</summary>
    [JsonPropertyName("useCount")]
    public int UseCount { get; set; }

    /// <summary>Sanitize and validate this snippet. Throws on invalid input.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Key))
            throw new ArgumentException("Key is required");
        if (Key.Length > 64)
            throw new ArgumentException($"Key too long: {Key.Length} > 64");
        if (Key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException($"Key contains whitespace or control chars: '{Key}'");
        if (string.IsNullOrEmpty(Content))
            throw new ArgumentException("Content is required");
    }
}
