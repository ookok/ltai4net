using System.Threading;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Context;

public sealed class CacheAlignerProvider : AIContextProvider
{
    private readonly ILogger<CacheAlignerProvider> _logger;
    private string? _cachedInstructions;
    private int _lastInstructionsHash;

    private long _normalizations;
    private long _cacheHits;
    private long _cacheMisses;
    private long _stableCount;

    public long Normalizations => Interlocked.Read(ref _normalizations);
    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public long CacheMisses => Interlocked.Read(ref _cacheMisses);
    public long StableCount => Interlocked.Read(ref _stableCount);
    public double HitRate
    {
        get
        {
            var total = Interlocked.Read(ref _normalizations);
            if (total == 0) return 0;
            return (double)Interlocked.Read(ref _cacheHits) / total;
        }
    }

    public CacheAlignerProvider(ILogger<CacheAlignerProvider> logger)
        : base(null, null, null)
    {
        _logger = logger;
    }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct)
    {
        var aiContext = context.AIContext;

        if (aiContext.Instructions != null)
        {
            Interlocked.Increment(ref _normalizations);
            var normalized = Normalize(aiContext.Instructions);
            var hash = normalized.GetHashCode();

            if (hash == _lastInstructionsHash && _cachedInstructions != null)
            {
                Interlocked.Increment(ref _cacheHits);
                Interlocked.Increment(ref _stableCount);
                aiContext.Instructions = _cachedInstructions;
            }
            else
            {
                Interlocked.Increment(ref _cacheMisses);
                Interlocked.Exchange(ref _stableCount, 0);
                _cachedInstructions = normalized;
                _lastInstructionsHash = hash;
            }
        }

        var tools = aiContext.Tools;
        if (tools != null && tools.Skip(1).Any())
        {
            var sorted = tools
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            aiContext.Tools = sorted.AsReadOnly();
        }

        return ValueTask.FromResult(aiContext);
    }

    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        var lastWasNewline = false;

        foreach (var c in s)
        {
            if (c == '\r') continue;
            if (c == '\n')
            {
                if (!lastWasNewline)
                {
                    sb.Append('\n');
                    lastWasNewline = true;
                }
            }
            else
            {
                sb.Append(c);
                lastWasNewline = false;
            }
        }

        var result = sb.ToString().TrimEnd('\n');
        return result + "\n\n";
    }
}
