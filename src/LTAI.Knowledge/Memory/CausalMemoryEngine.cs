using LTAI.DNA.Regulation;
using Microsoft.Extensions.Logging;

namespace LTAI.Knowledge.Memory;

public enum EpistemicSource { UserClaim, VerifiedFact, AgentDeduction, ExternalAuthority }

public sealed record CausalChain(
    string Cause, string Relation, string Effect, double Confidence);

public sealed class CausalMemoryEngine
{
    private readonly TemporalMemoryFabric _fabric;
    private readonly IRegulationProvider _regulationStore;
    private readonly ILogger<CausalMemoryEngine> _logger;

    public CausalMemoryEngine(
        TemporalMemoryFabric fabric,
        IRegulationProvider regulationStore,
        ILogger<CausalMemoryEngine> logger)
    {
        _fabric = fabric;
        _regulationStore = regulationStore;
        _logger = logger;
    }

    public async Task RecordMemoryAsync(MemoryEvent evt, EpistemicSource source, CancellationToken ct = default)
    {
        evt = evt with
        {
            Importance = source switch
            {
                EpistemicSource.VerifiedFact => 0.95,
                EpistemicSource.ExternalAuthority => 0.90,
                EpistemicSource.AgentDeduction => 0.60,
                EpistemicSource.UserClaim => 0.25,
                _ => 0.50
            },
            Metadata = new Dictionary<string, string>(evt.Metadata)
            {
                ["epistemic_source"] = source.ToString()
            }
        };

        _fabric.RecordEvent(evt);

        if (evt.GraphTriplet != null &&
            System.Text.RegularExpressions.Regex.IsMatch(evt.GraphTriplet, @"(GB|HJ)\s*\d{2,5}[-—]\d{4}"))
        {
            var codes = ExtractStandardCodes(evt.GraphTriplet);
            foreach (var code in codes)
            {
                try
                {
                    var regulation = await _regulationStore.GetActiveStandardAsync(code, DateTime.UtcNow, ct).ConfigureAwait(false);
                    if (regulation is null)
                        _logger.LogWarning("Memory pollution: standard {Code} not found in verified registry", code);
                }
                catch (RegulationSupersededException ex)
                {
                    _logger.LogWarning("Memory pollution: standard {Old} superseded by {New}", ex.OldCode, ex.NewCode);
                }
            }
        }
    }

    public async Task<List<CausalChain>> DeriveCausalChainsAsync(string eventId, CancellationToken ct = default)
    {
        var evt = _fabric.QueryTimeRange(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, count: 100)
            .FirstOrDefault(e => e.Id == eventId);
        if (evt == null) return new();

        var chains = new List<CausalChain>();
        var preceding = _fabric.QueryTimeRange(
            evt.Timestamp.AddMinutes(-30), evt.Timestamp, count: 20);

        if (evt.GraphTriplet != null && _fabric.GraphQueryAsync != null)
        {
            var related = await _fabric.GraphQueryAsync(evt.GraphTriplet).ConfigureAwait(false);
            foreach (var triple in related)
            {
                if (triple.relation.Contains("causes", StringComparison.OrdinalIgnoreCase) ||
                    triple.relation.Contains("triggers", StringComparison.OrdinalIgnoreCase) ||
                    triple.relation.Contains("导致", StringComparison.OrdinalIgnoreCase))
                {
                    chains.Add(new CausalChain(triple.subject, triple.relation, triple.obj, 0.85));
                }
            }
        }

        return chains.OrderByDescending(c => c.Confidence).Take(5).ToList();
    }

    public async Task<MemoryQueryResult?> FindAuthoritativeAnswerAsync(string query, CancellationToken ct)
    {
        var results = await _fabric.QueryAsync(query, topK: 10).ConfigureAwait(false);
        var allEvents = _fabric.QueryTimeRange(DateTime.UtcNow.AddHours(-24), DateTime.UtcNow, count: 1000);
        return results
            .Where(r =>
            {
                var evt = allEvents.FirstOrDefault(e => e.Id == r.Id);
                var source = evt?.Metadata.GetValueOrDefault("epistemic_source", "");
                return source != "UserClaim";
            })
            .MaxBy(r => r.Score);
    }

    private static List<string> ExtractStandardCodes(string text)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"(GB|HJ)\s*\d{2,5}[-—]\d{4}");
        return matches.Select(m => System.Text.RegularExpressions.Regex.Replace(m.Value, @"\s+", " ").Replace("—", "-")).ToList();
    }
}
