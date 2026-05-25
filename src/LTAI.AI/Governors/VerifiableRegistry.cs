using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiteDB;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class NumericMeasurement
{
    public ObjectId Id { get; set; } = default!;
    public string Key { get; set; } = "";
    public string Condition { get; set; } = "";
    public double Value { get; set; }
    public double? StdDev { get; set; }
    public string? Unit { get; set; }
    public string? SourceExperiment { get; set; }
    public long? SourceSeed { get; set; }
    public string Provenance { get; set; } = "";
    public string? Domain { get; set; }
    public DateTime MeasuredAt { get; set; } = DateTime.UtcNow;
    public bool IsVerified { get; set; }
}

public sealed class CitationRecord
{
    public ObjectId Id { get; set; } = default!;
    public string Title { get; set; } = "";
    public string? Doi { get; set; }
    public string? ArxivId { get; set; }
    public string? OpenAlexId { get; set; }
    public string? SemanticScholarId { get; set; }
    public CitationVerificationStatus Status { get; set; }
    public string? ResolutionSource { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}

public enum CitationVerificationStatus
{
    Pending,
    Verified,
    Suspicious,
    Hallucinated,
    ResolutionFailed
}

public sealed class ClaimVerificationResult
{
    public string Claim { get; set; } = "";
    public bool IsGrounded { get; set; }
    public double? ClaimedValue { get; set; }
    public NumericMeasurement? MatchedMeasurement { get; set; }
    public string? Discrepancy { get; set; }
}

public interface IVerifiableRegistry : IDisposable
{
    void RegisterMeasurement(NumericMeasurement measurement);
    void RegisterMeasurements(IEnumerable<NumericMeasurement> measurements);
    List<NumericMeasurement> GetMeasurementsByCondition(string condition);
    List<NumericMeasurement> GetMeasurementsByDomain(string domain);
    ClaimVerificationResult VerifyClaim(string claimText);
    List<ClaimVerificationResult> VerifyClaims(IEnumerable<string> claims);
    void RegisterCitation(CitationRecord citation);
    CitationRecord? GetCitation(string? doi = null, string? arxivId = null, string? title = null);
    int MeasurementCount { get; }
    int VerifiedCitationCount { get; }

    event Action<string, ClaimVerificationResult>? OnClaimRejected;
}

public sealed class VerifiableRegistry : IVerifiableRegistry
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<NumericMeasurement> _measurements;
    private readonly ILiteCollection<CitationRecord> _citations;
    private readonly Lock _lock = new();
    private readonly ILogger<VerifiableRegistry> _logger;
    private static readonly Regex NumberRegex = new(
        @"(?<!\w)([-+]?\d+\.?\d*(?:e[-+]?\d+)?)(?!\w)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public event Action<string, ClaimVerificationResult>? OnClaimRejected;

    public VerifiableRegistry(string dbPath, ILogger<VerifiableRegistry>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<VerifiableRegistry>.Instance;
        _db = new LiteDatabase($"Filename={dbPath};Connection=Shared");
        _measurements = _db.GetCollection<NumericMeasurement>("measurements");
        _citations = _db.GetCollection<CitationRecord>("citations");
        _measurements.EnsureIndex(x => x.Key);
        _measurements.EnsureIndex(x => x.Condition);
        _measurements.EnsureIndex(x => x.Domain);
        _measurements.EnsureIndex(x => x.MeasuredAt);
        _citations.EnsureIndex(x => x.Doi);
        _citations.EnsureIndex(x => x.ArxivId);
        _citations.EnsureIndex(x => x.Status);
    }

    public int MeasurementCount
    {
        get { lock (_lock) return _measurements.Count(); }
    }

    public int VerifiedCitationCount
    {
        get
        {
            lock (_lock) return _citations.Count(c => c.Status == CitationVerificationStatus.Verified);
        }
    }

    public void RegisterMeasurement(NumericMeasurement measurement)
    {
        measurement.Id = ObjectId.NewObjectId();
        measurement.MeasuredAt = DateTime.UtcNow;
        lock (_lock) _measurements.Insert(measurement);
    }

    public void RegisterMeasurements(IEnumerable<NumericMeasurement> measurements)
    {
        var now = DateTime.UtcNow;
        var list = measurements.ToList();
        foreach (var m in list)
        {
            m.Id = ObjectId.NewObjectId();
            m.MeasuredAt = now;
        }
        lock (_lock) _measurements.InsertBulk(list);
    }

    public List<NumericMeasurement> GetMeasurementsByCondition(string condition)
    {
        lock (_lock)
        {
            return _measurements.Find(m => m.Condition == condition)
                .OrderByDescending(m => m.MeasuredAt)
                .ToList();
        }
    }

    public List<NumericMeasurement> GetMeasurementsByDomain(string domain)
    {
        lock (_lock)
        {
            return _measurements.Find(m => m.Domain == domain)
                .OrderByDescending(m => m.MeasuredAt)
                .ToList();
        }
    }

    public ClaimVerificationResult VerifyClaim(string claimText)
    {
        var matches = NumberRegex.Matches(claimText);
        if (matches.Count == 0)
            return new ClaimVerificationResult { Claim = claimText, IsGrounded = true };

        foreach (Match match in matches)
        {
            if (!double.TryParse(match.Value, out var claimedValue))
                continue;

            var condition = ExtractCondition(claimText);
            var allMeasurements = string.IsNullOrEmpty(condition)
                ? GetAllMeasurementsSnapshot()
                : GetMeasurementsByCondition(condition);

            var matched = allMeasurements
                .Where(m => Math.Abs(m.Value - claimedValue) < Math.Abs(claimedValue) * 0.01
                         || Math.Abs(m.Value - claimedValue) < 1e-6)
                .OrderBy(m => Math.Abs(m.Value - claimedValue))
                .FirstOrDefault();

            if (matched != null)
            {
                return new ClaimVerificationResult
                {
                    Claim = claimText,
                    IsGrounded = true,
                    ClaimedValue = claimedValue,
                    MatchedMeasurement = matched
                };
            }

            var result = new ClaimVerificationResult
            {
                Claim = claimText,
                IsGrounded = false,
                ClaimedValue = claimedValue,
                Discrepancy = $"Value {claimedValue} not found in the verified registry (condition: {condition ?? "any"})"
            };
            OnClaimRejected?.Invoke(claimText, result);
            return result;
        }

        return new ClaimVerificationResult { Claim = claimText, IsGrounded = true };
    }

    public List<ClaimVerificationResult> VerifyClaims(IEnumerable<string> claims)
    {
        return claims.Select(VerifyClaim).ToList();
    }

    public void RegisterCitation(CitationRecord citation)
    {
        citation.Id = ObjectId.NewObjectId();
        citation.CheckedAt = DateTime.UtcNow;
        lock (_lock) _citations.Insert(citation);
    }

    public CitationRecord? GetCitation(string? doi = null, string? arxivId = null, string? title = null)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(doi))
                return _citations.FindOne(c => c.Doi == doi);
            if (!string.IsNullOrWhiteSpace(arxivId))
                return _citations.FindOne(c => c.ArxivId == arxivId);
            if (!string.IsNullOrWhiteSpace(title))
                return _citations.Find(c => c.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
            return null;
        }
    }

    private List<NumericMeasurement> GetAllMeasurementsSnapshot()
    {
        lock (_lock) return _measurements.FindAll().ToList();
    }

    private static string? ExtractCondition(string text)
    {
        var prefixMatch = Regex.Match(text, @"^(?:condition|group|mode|setting)[:\s]+(\S+)", RegexOptions.IgnoreCase);
        if (prefixMatch.Success) return prefixMatch.Groups[1].Value;

        var kvMatch = Regex.Match(text, @"(?:condition|group)=['""]?(\S+?)['""]?(?:[,;]|$)", RegexOptions.IgnoreCase);
        if (kvMatch.Success) return kvMatch.Groups[1].Value;

        return null;
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose database in VerifiableRegistry"); }
    }
}
