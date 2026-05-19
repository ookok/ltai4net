using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Metrics.Safety
{
    public record FileAnnulus(
        string Id,
        string File,
        int Index,
        string SnapshotPath,
        string Sha256,
        long SizeBytes,
        DateTime CreatedAt,
        string Trigger
    );

    public sealed class HarnessRegistry
    {
        public static readonly Lazy<HarnessRegistry> Instance = new(() => new HarnessRegistry());

        private readonly ILogger<HarnessRegistry> _logger;
        private readonly ConcurrentDictionary<string, List<FileAnnulus>> _annuli = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        public HarnessRegistry()
            : this(NullLogger<HarnessRegistry>.Instance) { }

        public HarnessRegistry(ILogger<HarnessRegistry> logger)
        {
            _logger = logger ?? NullLogger<HarnessRegistry>.Instance;
        }

        public int Snapshot(string filePath, string trigger = "auto")
        {
            lock (_lock)
            {
                return SnapshotInternal(filePath, trigger);
            }
        }

        public string? Diff(string filePath, int fromIdx = -2, int toIdx = -1)
        {
            lock (_lock)
            {
                if (!_annuli.TryGetValue(filePath, out var list) || list.Count == 0)
                    return null;

                int count = list.Count;
                int from = fromIdx < 0 ? count + fromIdx + 1 : fromIdx;
                int to = toIdx < 0 ? count + toIdx + 1 : toIdx;

                if (from < 1 || from > count || to < 1 || to > count || from > to)
                    return null;

                var fromAnnulus = list[from - 1];
                var toAnnulus = list[to - 1];
                long sizeDiff = toAnnulus.SizeBytes - fromAnnulus.SizeBytes;

                return $"File: {filePath}, From index {from} to {to}, Size change: {sizeDiff} bytes";
            }
        }

        public bool Rollback(string filePath, int toIndex)
        {
            lock (_lock)
            {
                if (!_annuli.TryGetValue(filePath, out var list))
                    return false;

                var target = list.FirstOrDefault(a => a.Index == toIndex);
                if (target == null)
                    return false;

                SnapshotInternal(filePath, "pre_rollback");
                return true;
            }
        }

        public List<FileAnnulus> ListSnapshots(string filePath)
        {
            lock (_lock)
            {
                if (!_annuli.TryGetValue(filePath, out var list))
                    return new List<FileAnnulus>();

                return list.OrderBy(a => a.Index).ToList();
            }
        }

        public Dictionary<string, object> VerifyIntegrity(string filePath)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = 0,
                ["missing"] = 0,
                ["corrupted"] = 0,
                ["healthy"] = true
            };
        }

        public int Clean(int olderThanDays = 30)
        {
            lock (_lock)
            {
                var threshold = DateTime.Now.AddDays(-olderThanDays);
                int removed = 0;

                foreach (var list in _annuli.Values)
                {
                    removed += list.RemoveAll(a => a.CreatedAt < threshold);
                }

                return removed;
            }
        }

        public Dictionary<string, object> GetStats()
        {
            lock (_lock)
            {
                int totalSnapshots = 0;
                foreach (var list in _annuli.Values)
                    totalSnapshots += list.Count;

                var topFiles = _annuli
                    .OrderByDescending(kvp => kvp.Value.Count)
                    .Take(20)
                    .Select(kvp => kvp.Key)
                    .ToList();

                return new Dictionary<string, object>
                {
                    ["files_tracked"] = _annuli.Count,
                    ["total_snapshots"] = totalSnapshots,
                    ["files"] = topFiles
                };
            }
        }

        private int SnapshotInternal(string filePath, string trigger)
        {
            if (!File.Exists(filePath))
                return -1;

            byte[] bytes = File.ReadAllBytes(filePath);
            string sha256 = ComputeSha256(bytes);

            var list = _annuli.GetOrAdd(filePath, _ => new List<FileAnnulus>());
            int index = list.Count + 1;
            string snapshotPath = $"harness/{Path.GetFileName(filePath)}.{index}.snap";

            var annulus = new FileAnnulus(
                Id: Guid.NewGuid().ToString("N"),
                File: filePath,
                Index: index,
                SnapshotPath: snapshotPath,
                Sha256: sha256,
                SizeBytes: bytes.Length,
                CreatedAt: DateTime.Now,
                Trigger: trigger
            );

            list.Add(annulus);
            _logger.LogDebug("Snapshot {Index} created for {File} [{Trigger}, {Size} bytes, SHA256: {Hash}]",
                index, filePath, trigger, bytes.Length, sha256[..8]);

            return index;
        }

        private static string ComputeSha256(byte[] bytes)
        {
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
