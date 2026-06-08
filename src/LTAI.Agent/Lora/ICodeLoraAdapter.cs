// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ICodeLoraAdapter — Code2LoRA hypernetwork adapter
//
//  Paper inspiration: Code2LoRA (arXiv 2606.06492)
//  Hypernetwork generates repo-specific LoRA weights on the fly,
//  no per-repo fine-tuning needed.
//
//  LTAI adaptation: instead of a full hypernetwork (GPU required),
//  we use a lightweight strategy: analyze the repo's file structure,
//  extract key patterns (class hierarchy, naming conventions,
//  common imports), and inject them as structured conversation
//  prefix via the pipeline.
// ═══════════════════════════════════════════════════════════════

namespace LTAI.Agent.Lora;

/// <summary>
/// Code-aware adapter that extracts repository-level context
/// and injects it into the agent's system prompt / conversation
/// prefix, simulating a LoRA-style adaptation without actual
/// weight modification.
///
/// Strategies (configurable via LTAI:Lora:Strategy):
///   - "prompt-inject" — extract repo structure → structured prefix
///   - "system-prompt" — embed patterns into agent system prompt
///   - (future) "hypernetwork" — actual LoRA weight generation
/// </summary>
public interface ICodeLoraAdapter : IDisposable
{
    /// <summary>Name of the adapter (e.g. repo path hash).</summary>
    string Name { get; }

    /// <summary>Current strategy being used.</summary>
    string Strategy { get; }

    /// <summary>
    /// Analyze a repository directory and compute code context.
    /// Should be called when a user opens a new workspace.
    /// </summary>
    Task<CodeContext> AnalyzeAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Generate structured prefix text for the current code context.
    /// This text is injected into the agent's conversation to
    /// "adapt" it to the specific codebase.
    /// </summary>
    Task<string> GeneratePrefixAsync(CancellationToken ct = default);

    /// <summary>
    /// Invalidate the cached context for a repo path.
    /// </summary>
    void Invalidate(string repoPath);
}

/// <summary>
/// Code context data extracted from a repository.
/// </summary>
public sealed record CodeContext
{
    /// <summary>Repository root path.</summary>
    public string RepoPath { get; init; } = "";

    /// <summary>Programming languages detected.</summary>
    public IReadOnlySet<string> Languages { get; init; } = new HashSet<string>();

    /// <summary>Key project file names (e.g. .csproj, package.json).</summary>
    public IReadOnlyList<string> ProjectFiles { get; init; } = [];

    /// <summary>Top-level namespace / module hierarchy.</summary>
    public IReadOnlyList<string> Namespaces { get; init; } = [];

    /// <summary>Common imports / usings across files.</summary>
    public IReadOnlyList<string> CommonImports { get; init; } = [];

    /// <summary>File count summary by language.</summary>
    public IReadOnlyDictionary<string, int> FileCountByLang { get; init; }
        = new Dictionary<string, int>();

    /// <summary>Total source files analyzed.</summary>
    public int TotalFiles { get; init; }

    /// <summary>Lines of code (approximate).</summary>
    public long LoC { get; init; }

    /// <summary>When this context was extracted.</summary>
    public DateTime AnalyzedAt { get; init; } = DateTime.UtcNow;
}
