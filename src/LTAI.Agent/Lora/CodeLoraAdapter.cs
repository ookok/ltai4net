// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  CodeLoraAdapter — prompt-inject Code2LoRA implementation
//
//  Analyzes a repository and generates structured conversation
//  prefix that adapts the agent to the specific codebase.
//  Cached per-repo with TTL.
//
//  Paper: Code2LoRA (arXiv 2606.06492)
//  "Hypernetwork-generated LoRA adapters for code agents"
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;

namespace LTAI.Agent.Lora;

/// <summary>
/// Code2LoRA adapter implementation using prompt-inject strategy.
/// Caches analyzed context per repository path with configurable TTL.
///
/// Strategy: "prompt-inject" (no GPU needed)
///   1. RepoAnalyzer extracts structure metadata
///   2. Formats into a structured prefix
///   3. Prefix is cached and injected via LoraAdapterStep
///
/// Thread-safe after construction.
/// </summary>
public sealed class CodeLoraAdapter : ICodeLoraAdapter
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly RepoAnalyzer _analyzer = new();
    private readonly TimeSpan _cacheTtl;
    private bool _disposed;

    public string Name => "Code2LoRA";
    public string Strategy { get; }

    /// <summary>Number of cached contexts.</summary>
    public int CachedCount => _cache.Count;

    private sealed class CacheEntry
    {
        public CodeContext Context;
        public string? Prefix;
        public DateTime CachedAt;

        public CacheEntry(CodeContext context)
        {
            Context = context;
            CachedAt = DateTime.UtcNow;
        }

        public bool IsExpired(TimeSpan ttl) => DateTime.UtcNow - CachedAt > ttl;
    }

    public CodeLoraAdapter(TimeSpan? cacheTtl = null, string strategy = "prompt-inject")
    {
        _cacheTtl = cacheTtl ?? TimeSpan.FromMinutes(30);
        Strategy = strategy;
    }

    /// <inheritdoc />
    public async Task<CodeContext> AnalyzeAsync(string repoPath, CancellationToken ct = default)
    {
        // Check cache
        if (_cache.TryGetValue(repoPath, out var entry) && !entry.IsExpired(_cacheTtl))
            return entry.Context;

        // Analyze
        var context = await _analyzer.AnalyzeAsync(repoPath, ct).ConfigureAwait(false);

        // Cache
        _cache[repoPath] = new CacheEntry(context)
        {
            Prefix = context.TotalFiles > 0 ? BuildPrefix(context) : null
        };

        return context;
    }

    /// <inheritdoc />
    public Task<string> GeneratePrefixAsync(CancellationToken ct = default)
    {
        // Return prefix for the most recently analyzed repo
        var latest = _cache.Values
            .Where(e => e.Prefix != null)
            .OrderByDescending(e => e.CachedAt)
            .FirstOrDefault();

        return Task.FromResult(latest?.Prefix ?? "");
    }

    /// <summary>
    /// Generate prefix for a specific repo path.
    /// </summary>
    public async Task<string> GeneratePrefixAsync(string repoPath, CancellationToken ct = default)
    {
        var context = await AnalyzeAsync(repoPath, ct).ConfigureAwait(false);
        if (context.TotalFiles == 0) return "";

        if (_cache.TryGetValue(repoPath, out var entry) && entry.Prefix != null)
            return entry.Prefix;

        var prefix = BuildPrefix(context);
        if (_cache.TryGetValue(repoPath, out entry))
            entry.Prefix = prefix;
        return prefix;
    }

    /// <inheritdoc />
    public void Invalidate(string repoPath)
        => _cache.TryRemove(repoPath, out _);

    /// <summary>
    /// Build a structured conversation prefix from code context.
    /// </summary>
    private static string BuildPrefix(CodeContext ctx)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("[Repository Context]");
        sb.AppendLine($"- Languages: {string.Join(", ", ctx.Languages)}");
        sb.AppendLine($"- Files: {ctx.TotalFiles} ({ctx.LoC:N0} LOC)");

        if (ctx.ProjectFiles.Count > 0)
        {
            sb.AppendLine("- Projects:");
            foreach (var pf in ctx.ProjectFiles.Take(10))
                sb.AppendLine($"  - {Path.GetFileName(pf)}");
        }

        if (ctx.Namespaces.Count > 0)
        {
            sb.AppendLine("- Key Namespaces/Modules:");
            foreach (var ns in ctx.Namespaces.Take(10))
                sb.AppendLine($"  - {ns}");
        }

        if (ctx.CommonImports.Count > 0)
        {
            sb.AppendLine("- Common Dependencies:");
            foreach (var imp in ctx.CommonImports.Take(10))
                sb.AppendLine($"  - {imp}");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cache.Clear();
    }
}
