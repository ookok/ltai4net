namespace LTAI.Core.Caching;

public sealed class LTAICacheOptions
{
    public long? MaxSizeBytes { get; init; }
    public int? MaxEntries { get; init; }
    public TimeSpan DefaultTtl { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan EvictionInterval { get; init; } = TimeSpan.FromSeconds(30);
}
