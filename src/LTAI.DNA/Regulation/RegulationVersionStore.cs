using System.Collections.Concurrent;

namespace LTAI.DNA.Regulation;

public sealed record Regulation(
    string Code,
    string Title,
    string Domain,
    DateTime EffectiveFrom,
    DateTime? SupersededOn,
    bool IsActive,
    string? SupersededBy,
    string OfficialChecksum,
    DateTime LastVerifiedDate);

public sealed record IntegrityReport(
    List<string> ExpiredStandards,
    List<StaleRegulation> StaleVerifications,
    List<IntegrityViolation> IntegrityViolations);

public sealed record StaleRegulation(string Code, DateTime LastVerified);

public sealed record IntegrityViolation(string Code, string LocalChecksum, string OfficialChecksum);

public interface IRegulationProvider
{
    Task<Regulation?> GetActiveStandardAsync(string code, DateTime effectiveDate, CancellationToken ct);
    Task<IReadOnlyList<Regulation>> SearchAsync(string keyword, CancellationToken ct);
    bool IsValidCode(string code);
    Task<IntegrityReport> VerifyIntegrityAsync(CancellationToken ct);
}

public sealed class RegulationSupersededException : Exception
{
    public string OldCode { get; }
    public string NewCode { get; }
    public RegulationSupersededException(string oldCode, string newCode)
        : base($"Regulation {oldCode} has been superseded by {newCode}")
    {
        OldCode = oldCode;
        NewCode = newCode;
    }
}

public sealed class RegulationNotFoundException : Exception
{
    public RegulationNotFoundException(string code)
        : base($"Regulation {code} not found in verified standards database") { }
}

public sealed class RegulationVersionStore : IRegulationProvider
{
    private readonly ConcurrentDictionary<string, Regulation> _standards = new();
    private static readonly TimeSpan VerificationExpiry = TimeSpan.FromDays(90);
    private static readonly TimeSpan SupersededGraceMonths = TimeSpan.FromDays(180);

    public RegulationVersionStore()
    {
        SeedStandards();
    }

    public async Task<Regulation?> GetActiveStandardAsync(
        string code, DateTime effectiveDate, CancellationToken ct)
    {
        if (_standards.TryGetValue(code, out var reg) && reg.IsActive && effectiveDate >= reg.EffectiveFrom)
            return await Task.FromResult(reg);

        var superseded = _standards.Values.FirstOrDefault(r => r.SupersededBy == code);
        if (superseded != null)
            throw new RegulationSupersededException(superseded.Code, code);

        return null;
    }

    public Task<IReadOnlyList<Regulation>> SearchAsync(string keyword, CancellationToken ct)
    {
        var results = _standards.Values
            .Where(r => r.IsActive &&
                        (r.Code.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                         r.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return Task.FromResult<IReadOnlyList<Regulation>>(results);
    }

    public bool IsValidCode(string code) =>
        _standards.TryGetValue(code, out var reg) && reg.IsActive;

    public async Task<IntegrityReport> VerifyIntegrityAsync(CancellationToken ct)
    {
        var expired = new List<string>();
        var stale = new List<StaleRegulation>();
        var integrity = new List<IntegrityViolation>();

        foreach (var (code, reg) in _standards)
        {
            if (reg.SupersededOn.HasValue &&
                DateTime.UtcNow > reg.SupersededOn.Value.Add(SupersededGraceMonths))
                expired.Add(code);

            if ((DateTime.UtcNow - reg.LastVerifiedDate) > VerificationExpiry)
                stale.Add(new StaleRegulation(code, reg.LastVerifiedDate));

            var liveChecksum = await FetchOfficialChecksumAsync(code, ct);
            if (liveChecksum != null && liveChecksum != reg.OfficialChecksum)
                integrity.Add(new IntegrityViolation(code, reg.OfficialChecksum, liveChecksum));
        }

        return await Task.FromResult(new IntegrityReport(expired, stale, integrity));
    }

    public void AddOrUpdate(Regulation regulation)
    {
        _standards[regulation.Code] = regulation;
    }

    private static async Task<string?> FetchOfficialChecksumAsync(string code, CancellationToken ct)
    {
        await Task.CompletedTask;
        return null; // Phase 1: stub — Phase 2 integrates with real MEE API
    }

    private void SeedStandards()
    {
        var now = DateTime.UtcNow;
        var standards = new[]
        {
            new Regulation("GB 3095-2012", "环境空气质量标准", "air",
                new DateTime(2012, 2, 29), null, true, null,
                "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", now),
            new Regulation("GB 3838-2002", "地表水环境质量标准", "water",
                new DateTime(2002, 4, 28), null, true, null,
                "sha256:placeholder", now),
            new Regulation("GB 3096-2008", "声环境质量标准", "noise",
                new DateTime(2008, 8, 19), null, true, null,
                "sha256:placeholder", now),
            new Regulation("HJ 2.2-2018", "环境影响评价技术导则 大气环境", "air",
                new DateTime(2018, 7, 31), null, true, null,
                "sha256:placeholder", now),
            new Regulation("HJ 2.1-2016", "环境影响评价技术导则 总纲", "general",
                new DateTime(2016, 11, 1), null, true, null,
                "sha256:placeholder", now),
            new Regulation("HJ 2.4-2021", "环境影响评价技术导则 声环境", "noise",
                new DateTime(2021, 12, 1), null, true, null,
                "sha256:placeholder", now),
            new Regulation("HJ 610-2016", "环境影响评价技术导则 地下水", "water",
                new DateTime(2016, 1, 7), null, true, null,
                "sha256:placeholder", now),
            new Regulation("HJ 19-2022", "环境影响评价技术导则 生态影响", "ecological",
                new DateTime(2022, 1, 15), null, true, null,
                "sha256:placeholder", now),
            new Regulation("HJ 169-2018", "建设项目环境风险评价技术导则", "risk",
                new DateTime(2018, 10, 15), null, true, null,
                "sha256:placeholder", now),
        };

        foreach (var s in standards)
            _standards[s.Code] = s;
    }
}
