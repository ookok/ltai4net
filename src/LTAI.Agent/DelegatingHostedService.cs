using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent;

public sealed class DelegatingHostedService : IHostedService, IDisposable
{
    private readonly string _name;
    private readonly Func<CancellationToken, Task> _onStart;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;
    private Timer? _timer;

    public DelegatingHostedService(string name, Func<CancellationToken, Task> onStart,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        _name = name;
        _onStart = onStart;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await _onStart(ct).ConfigureAwait(false);
            _logger.LogInformation("DelegatingHostedService '{Name}': started", _name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DelegatingHostedService '{Name}': start failed", _name);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public void Dispose() => _timer?.Dispose();
}
