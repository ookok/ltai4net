using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace LTAI.Core.Caching;

public sealed class LTAICacheMetrics
{
    private long _hits, _misses, _evictions;

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long Evictions => Interlocked.Read(ref _evictions);
    public long Lookups => Hits + Misses;
    public double HitRate => Lookups == 0 ? 0d : (double)Hits / Lookups;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordHit() => Interlocked.Increment(ref _hits);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordMiss() => Interlocked.Increment(ref _misses);

    public long RecordEvictions(int count) =>
        Interlocked.Add(ref _evictions, count);

    public void Reset()
    {
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
        Interlocked.Exchange(ref _evictions, 0);
    }

    public string Summary =>
        $"Hits={Hits} Misses={Misses} HitRate={HitRate:P1} Evictions={Evictions}";
}
