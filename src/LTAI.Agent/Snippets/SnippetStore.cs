// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  SnippetStore — Persistent store for user-defined "common phrases"
//
//  Storage:  <DataDirectory>/snippets.json   (one JSON array)
//  Concurrency: SemaphoreSlim(1,1) protects file I/O
//  Corruption recovery:  broken file → .bak + empty
//  Shared:  LTAI.TUI and LTAI.Desktop both read/write the same file.
//
//  D62  Storage lives in LTAI.Agent (LTAI.Core is dependency-free).
//  D63  No encryption (low-sensitivity; matches skill_usage.json).
//  D65  Delete is hard (no recycle bin).
// ═══════════════════════════════════════════════════════════════

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Snippets;

public sealed class SnippetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;
    private readonly ILogger<SnippetStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<Snippet> _cache = [];

    public SnippetStore(string filePath, ILogger<SnippetStore>? logger = null)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _logger = logger ?? NullLogger<SnippetStore>.Instance;
    }

    /// <summary>All known snippets, sorted by key. Loads from disk on first call.</summary>
    public async Task<IReadOnlyList<Snippet>> ListAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        return _cache.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Look up by exact key (case-insensitive). Returns null if not found.</summary>
    public async Task<Snippet?> GetAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        return _cache.FirstOrDefault(s =>
            string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Add a new snippet, or update an existing one (overwrite content / description).
    /// Validates the snippet; throws ArgumentException on invalid input.
    /// </summary>
    public async Task SaveAsync(Snippet snippet, CancellationToken ct = default)
    {
        if (snippet == null) throw new ArgumentNullException(nameof(snippet));
        snippet.Validate();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedInternalAsync(ct).ConfigureAwait(false);

            var existingIdx = _cache.FindIndex(s =>
                string.Equals(s.Key, snippet.Key, StringComparison.OrdinalIgnoreCase));

            if (existingIdx >= 0)
            {
                // Update: preserve createdAt + useCount + lastUsedAt
                var existing = _cache[existingIdx];
                _cache[existingIdx] = new Snippet
                {
                    Key = snippet.Key,
                    Content = snippet.Content,
                    Description = snippet.Description,
                    CreatedAt = existing.CreatedAt,
                    LastUsedAt = existing.LastUsedAt,
                    UseCount = existing.UseCount,
                };
                _logger.LogInformation("Updated snippet '{Key}'", snippet.Key);
            }
            else
            {
                _cache.Add(new Snippet
                {
                    Key = snippet.Key,
                    Content = snippet.Content,
                    Description = snippet.Description,
                    CreatedAt = DateTime.UtcNow,
                });
                _logger.LogInformation("Added snippet '{Key}'", snippet.Key);
            }

            await PersistUnsafeAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Delete a snippet by key. Returns true if found and removed, false if not found.
    /// </summary>
    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedInternalAsync(ct).ConfigureAwait(false);

            var removed = _cache.RemoveAll(s =>
                string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;

            await PersistUnsafeAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Deleted snippet '{Key}'", key);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Rename a snippet's key atomically. Returns false if old key not found.
    /// Throws if new key already exists.
    /// </summary>
    public async Task<bool> RenameAsync(string oldKey, string newKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(oldKey) || string.IsNullOrWhiteSpace(newKey)) return false;
        if (string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase)) return true;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedInternalAsync(ct).ConfigureAwait(false);

            var existing = _cache.FirstOrDefault(s =>
                string.Equals(s.Key, oldKey, StringComparison.OrdinalIgnoreCase));
            if (existing == null) return false;

            if (_cache.Any(s => string.Equals(s.Key, newKey, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Snippet key '{newKey}' already exists");

            existing.Key = newKey;
            await PersistUnsafeAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Renamed snippet '{Old}' → '{New}'", oldKey, newKey);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Bump use-count + last-used timestamp for a snippet. Returns true if found.
    /// </summary>
    public async Task<bool> TouchAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedInternalAsync(ct).ConfigureAwait(false);

            var existing = _cache.FirstOrDefault(s =>
                string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
            if (existing == null) return false;

            existing.UseCount++;
            existing.LastUsedAt = DateTime.UtcNow;
            await PersistUnsafeAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Internal ─────────────────────────────────────────────

    private Task EnsureLoadedAsync(CancellationToken ct) => EnsureLoadedInternalAsync(ct);

    private Task EnsureLoadedInternalAsync(CancellationToken ct)
    {
        if (_cache.Count > 0 || !File.Exists(_filePath)) return Task.CompletedTask;
        return LoadFromDiskAsync(ct);
    }

    private async Task LoadFromDiskAsync(CancellationToken ct)
    {
        try
        {
            var text = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<List<Snippet>>(text, JsonOptions);
            _cache = parsed ?? [];
            _logger.LogInformation("Loaded {Count} snippet(s) from {Path}",
                _cache.Count, _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load snippets from {Path} — starting empty", _filePath);
            // Backup the corrupt file so the user can recover manually
            try
            {
                if (File.Exists(_filePath))
                {
                    var bak = _filePath + ".bak";
                    File.Move(_filePath, bak, overwrite: true);
                }
            }
            catch { /* best effort */ }
            _cache = [];
        }
    }

    private async Task PersistUnsafeAsync(CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Atomic write: serialize to temp file, then move into place
            var tmp = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(_cache, JsonOptions);
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist snippets to {Path}", _filePath);
            throw;
        }
    }
}
