namespace LTAI.Agent.Formats;

/// <summary>
/// Output format for knowledge graph query results.
/// Markdown — human-readable (default, existing behavior).
/// Toon    — Token-Oriented Object Notation, ~50% token reduction vs Markdown.
/// </summary>
public enum ResultFormat
{
    Markdown,
    Toon,
}
