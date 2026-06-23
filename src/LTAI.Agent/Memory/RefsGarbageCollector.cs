// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  RefsGarbageCollector — TTL-based cleanup of .livingtree/refs/
//
//  Scans refs directory on a configurable interval, removes files
//  past TTL or exceeding max count. Files with active refs in the
//  current session (tracked via usage timestamps) are preserved.
// ═══════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Memory;

public sealed class RefsGarbageCollector : IDisposable
{
    private readonly string _refsDir;
    private readonly ILogger<RefsGarbageCollector> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _recentlyUsed = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;

    public RefsGarbageCollector(
        string refsDir,
        ILogger<RefsGarbageCollector>? logger = null,
        int cleanupIntervalMinutes = 60,
        int ttlHours = 24,
        int maxFiles = 10000)
    {
        _refsDir = refsDir;
        _logger = logger ?? NullLogger<RefsGarbageCollector>.Instance;
        _timer = new Timer(async _ => await CleanupAsync(ttlHours, maxFiles).ConfigureAwait(false),
            null, TimeSpan.FromMinutes(cleanupIntervalMinutes), TimeSpan.FromMinutes(cleanupIntervalMinutes));
        _logger.LogInformation(
            "RefsGarbageCollector: started (interval={Interval}m, ttl={Ttl}h, maxFiles={Max})",
            cleanupIntervalMinutes, ttlHours, maxFiles);
    }

    public void MarkUsed(string refId)
    {
        _recentlyUsed[refId] = DateTime.UtcNow;
    }

    public async Task CleanupAsync(int ttlHours = 24, int maxFiles = 10000)
    {
        if (!Directory.Exists(_refsDir))
        {
            Directory.CreateDirectory(_refsDir);
            return;
        }

        var cutoff = DateTime.UtcNow.AddHours(-ttlHours);
        var files = Directory.GetFiles(_refsDir, "*.md")
            .Select(f => (Path: f, LastWrite: File.GetLastWriteTimeUtc(f)))
            .OrderByDescending(f => f.LastWrite)
            .ToList();

        int deleted = 0;
        int kept = 0;

        // Phase 1: TTL-based eviction (old files past TTL)
        foreach (var file in files)
        {
            var name = Path.GetFileName(file.Path);
            if (name == "index.md") continue; // never delete index

            // Skip if recently used in current session
            if (_recentlyUsed.TryGetValue(name, out var used) && used > cutoff)
            {
                kept++;
                continue;
            }

            if (file.LastWrite < cutoff)
            {
                try
                {
                    File.Delete(file.Path);
                    deleted++;
                    _recentlyUsed.TryRemove(name, out _);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "RefsGarbageCollector: failed to delete {File}", file.Path);
                }
            }
            else
            {
                kept++;
            }
        }

        // Phase 2: Max-files eviction (if still over limit after TTL pass)
        var remaining = Directory.GetFiles(_refsDir, "*.md")
            .Select(f => (Path: f, LastWrite: File.GetLastWriteTimeUtc(f)))
            .OrderByDescending(f => f.LastWrite)
            .ToList();

        if (remaining.Count > maxFiles)
        {
            var toRemove = remaining.Skip(maxFiles).ToList();
            foreach (var file in toRemove)
            {
                var name = Path.GetFileName(file.Path);
                if (_recentlyUsed.ContainsKey(name)) continue;
                try
                {
                    File.Delete(file.Path);
                    deleted++;
                }
                catch { }
            }
            kept = maxFiles;
        }

        if (deleted > 0 || kept > 0)
        {
            _logger.LogInformation(
                "RefsGarbageCollector: cleanup done — deleted {Deleted}, kept {Kept} (ttl={Ttl}h, maxFiles={Max})",
                deleted, kept, ttlHours, maxFiles);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
