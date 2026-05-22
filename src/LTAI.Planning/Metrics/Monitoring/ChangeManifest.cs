using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTAI.Planning.Metrics.Monitoring
{
    public enum ChangeType
    {
        Add,
        Modify,
        Delete,
        Refactor,
        Fix
    }

    public enum VerificationStatus
    {
        Pending,
        Verified,
        Falsified,
        Partial
    }

    public record ChangeEntry(
        string Id,
        string File,
        ChangeType ChangeType,
        string Description,
        string? PredictedOutcome,
        string? ActualOutcome,
        VerificationStatus Status,
        bool Success,
        double Score,
        List<string> Tags,
        string? ParentSpanId,
        DateTime CreatedAt,
        DateTime? VerifiedAt)
    {
        public ChangeEntry(
            string file,
            ChangeType changeType,
            string description,
            string? predictedOutcome = null,
            List<string>? tags = null,
            string? parentSpanId = null)
            : this(
                "chg_" + Guid.NewGuid().ToString("N").Substring(0, 12),
                file,
                changeType,
                description,
                predictedOutcome,
                null,
                VerificationStatus.Pending,
                false,
                0,
                tags ?? new List<string>(),
                parentSpanId,
                DateTime.UtcNow,
                null)
        {
        }

        public ChangeEntry WithStatus(VerificationStatus status, bool success, double score, string? actualOutcome)
            => this with
            {
                Status = status,
                Success = success,
                Score = score,
                ActualOutcome = actualOutcome,
                VerifiedAt = DateTime.UtcNow
            };
    }

    public sealed class ChangeManifest
    {
        public static readonly Lazy<ChangeManifest> Instance = new(() => new ChangeManifest());

        private readonly ILogger<ChangeManifest> _logger;
        private readonly ConcurrentDictionary<string, ChangeEntry> _entries = new();
        private readonly object _lock = new();

        public ChangeManifest() : this(NullLogger<ChangeManifest>.Instance)
        {
        }

        public ChangeManifest(ILogger<ChangeManifest> logger)
        {
            _logger = logger ?? NullLogger<ChangeManifest>.Instance;
        }

        public string Record(
            string file,
            ChangeType changeType,
            string description,
            string? predictedOutcome = null,
            List<string>? tags = null,
            string? parentSpanId = null)
        {
            var entry = new ChangeEntry(file, changeType, description, predictedOutcome, tags, parentSpanId);
            _entries[entry.Id] = entry;
            _logger.LogInformation("Recorded change {ChangeId} in {File}: {Description}", entry.Id, entry.File, entry.Description);
            return entry.Id;
        }

        public bool Verify(string changeId, bool success, string? actualOutcome = null, double score = 0)
        {
            if (!_entries.TryGetValue(changeId, out var entry))
            {
                _logger.LogWarning("Attempted to verify unknown change id {ChangeId}", changeId);
                return false;
            }

            lock (_lock)
            {
                if (!_entries.TryGetValue(changeId, out entry))
                    return false;

                VerificationStatus status;
                if (score >= 0.8)
                    status = VerificationStatus.Verified;
                else if (!success)
                    status = VerificationStatus.Falsified;
                else
                    status = VerificationStatus.Partial;

                var updated = entry.WithStatus(status, success, score, actualOutcome);
                _entries[changeId] = updated;
                _logger.LogInformation("Verified change {ChangeId} as {Status}", changeId, status);
                return true;
            }
        }

        public ChangeEntry? Get(string changeId)
        {
            _entries.TryGetValue(changeId, out var entry);
            return entry;
        }

        public List<ChangeEntry> GetUnverified()
        {
            return _entries.Values
                .Where(e => e.Status == VerificationStatus.Pending)
                .ToList();
        }

        public List<ChangeEntry> GetFalsified(int limit = 20)
        {
            return _entries.Values
                .Where(e => e.Status == VerificationStatus.Falsified)
                .Take(limit)
                .ToList();
        }

        public List<ChangeEntry> GetByFile(string file)
        {
            return _entries.Values
                .Where(e => e.File.Equals(file, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public List<ChangeEntry> GetRecent(int n = 20)
        {
            return _entries.Values
                .OrderByDescending(e => e.CreatedAt)
                .Take(n)
                .ToList();
        }

        public Dictionary<string, object> GetVerificationReport()
        {
            var all = _entries.Values.ToList();
            var total = all.Count;
            var verified = all.Count(e => e.Status == VerificationStatus.Verified);
            var falsified = all.Count(e => e.Status == VerificationStatus.Falsified);
            var pending = all.Count(e => e.Status == VerificationStatus.Pending);
            var partial = all.Count(e => e.Status == VerificationStatus.Partial);

            var verifiedEntries = all.Where(e => e.Status == VerificationStatus.Verified).ToList();
            var avgScore = verifiedEntries.Count > 0 ? verifiedEntries.Average(e => e.Score) : 0.0;

            return new Dictionary<string, object>
            {
                ["total"] = total,
                ["verified_count"] = verified,
                ["verified_rate"] = total > 0 ? (double)verified / total : 0,
                ["falsified_count"] = falsified,
                ["falsified_rate"] = total > 0 ? (double)falsified / total : 0,
                ["pending_count"] = pending,
                ["partial_count"] = partial,
                ["average_score"] = Math.Round(avgScore, 4),
                ["per_status"] = new Dictionary<string, int>
                {
                    ["Verified"] = verified,
                    ["Falsified"] = falsified,
                    ["Pending"] = pending,
                    ["Partial"] = partial
                }
            };
        }

        public Dictionary<string, Dictionary<string, object>> GetStatsByFile()
        {
            return _entries.Values
                .GroupBy(e => e.File)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var changes = g.ToList();
                        var total = changes.Count;
                        var verified = changes.Count(e => e.Status == VerificationStatus.Verified);
                        var avgScore = changes
                            .Where(e => e.Status == VerificationStatus.Verified)
                            .Select(e => e.Score)
                            .DefaultIfEmpty(0)
                            .Average();

                        return new Dictionary<string, object>
                        {
                            ["change_count"] = total,
                            ["verified_rate"] = total > 0 ? (double)verified / total : 0,
                            ["average_score"] = Math.Round(avgScore, 4)
                        };
                    });
        }

        public Dictionary<string, object> GetStats()
        {
            var all = _entries.Values.ToList();
            return new Dictionary<string, object>
            {
                ["entry_count"] = all.Count,
                ["status_breakdown"] = new Dictionary<string, int>
                {
                    ["Verified"] = all.Count(e => e.Status == VerificationStatus.Verified),
                    ["Falsified"] = all.Count(e => e.Status == VerificationStatus.Falsified),
                    ["Pending"] = all.Count(e => e.Status == VerificationStatus.Pending),
                    ["Partial"] = all.Count(e => e.Status == VerificationStatus.Partial)
                }
            };
        }
    }
}
