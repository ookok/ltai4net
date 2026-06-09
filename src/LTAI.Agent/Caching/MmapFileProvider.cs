using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Caching;

public sealed class MmapFileProvider
{
    private readonly MmapCache _cache;
    private readonly ILogger _logger;

    public MmapFileProvider(MmapCache? cache = null, ILogger<MmapFileProvider>? logger = null)
    {
        _cache = cache ?? new MmapCache();
        _logger = logger ?? NullLogger<MmapFileProvider>.Instance;
    }

    public bool IsAvailable => true;

    public int CachedCount => _cache.CachedCount;
    public long CachedBytes => _cache.TotalBytes;

    public async Task<string> ReadAllTextAsync(string path)
    {
        var cached = _cache.ReadAllText(path);
        if (cached != null) return cached;

        // No cache decision yet — read from disk and let MmapCache track access
        var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        return text;
    }

    public string ReadAllText(string path)
    {
        var cached = _cache.ReadAllText(path);
        if (cached != null) return cached;

        var text = File.ReadAllText(path);
        return text;
    }

    public Stream OpenRead(string path)
    {
        return _cache.OpenReadStream(path) ?? File.OpenRead(path);
    }

    public void Invalidate(string path)
    {
        _cache.Invalidate(path);
    }
}
