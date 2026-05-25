using LTAI.Agent.Routing;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Routing;

public sealed class ShadowRouter
{
    private readonly UnifiedSemanticRouter _newRouter;
    private readonly IntentRouter _legacyRouter;
    private readonly ILogger<ShadowRouter> _logger;
    private readonly string _statePath;
    private int _totalRoutes;
    private int _agreedRoutes;
    private const double PromotionThreshold = 0.99;

    public ShadowRouter(
        UnifiedSemanticRouter newRouter,
        IntentRouter legacyRouter,
        ILogger<ShadowRouter> logger)
    {
        _newRouter = newRouter;
        _legacyRouter = legacyRouter;
        _logger = logger;
        _statePath = Path.Combine(".livingtree", "shadow", "routes.jsonl");
        var dir = Path.GetDirectoryName(_statePath);
        if (dir != null) Directory.CreateDirectory(dir);
    }

    public async Task<SemanticRoute> RouteWithShadowAsync(string text, CancellationToken ct = default)
    {
        var legacyResult = _legacyRouter.Classify(text);
        var newResult = await _newRouter.RouteAsync(text, ct).ConfigureAwait(false);

        var agreed = newResult.TargetAgent == legacyResult.TargetAgent;
        Interlocked.Increment(ref _totalRoutes);
        if (agreed) Interlocked.Increment(ref _agreedRoutes);

        var accuracy = GetAccuracy();
        if (_totalRoutes % 100 == 0)
            _logger.LogInformation("ShadowRouter: accuracy={Acc:P2} ({Agreed}/{Total})", accuracy, _agreedRoutes, _totalRoutes);

        if (_totalRoutes >= 10000 && accuracy >= PromotionThreshold)
        {
            _logger.LogInformation("ShadowRouter: accuracy {Acc:P2} >= 99%, ready for active promotion", accuracy);
            await File.AppendAllTextAsync(_statePath,
                $"{{\"ts\":\"{DateTime.UtcNow:O}\",\"event\":\"PROMOTE_READY\",\"accuracy\":{accuracy:F4},\"total\":{_totalRoutes}}}\n", ct);
        }

        return newResult;
    }

    public double GetAccuracy() =>
        _totalRoutes > 0 ? (double)_agreedRoutes / _totalRoutes : 1.0;

    public (int total, int agreed, double accuracy) GetStats()
    {
        var t = _totalRoutes;
        var a = _agreedRoutes;
        return (t, a, t > 0 ? (double)a / t : 0);
    }
}
