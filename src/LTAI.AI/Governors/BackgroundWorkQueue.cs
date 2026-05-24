using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class BackgroundWorkQueue : IAsyncDisposable
{
    private readonly Channel<WorkItem> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<BackgroundWorkQueue>? _logger;
    private readonly Task _consumer;
    private int _enqueued;
    private int _completed;
    private int _failed;

    public int EnqueuedCount => Volatile.Read(ref _enqueued);
    public int CompletedCount => Volatile.Read(ref _completed);
    public int FailedCount => Volatile.Read(ref _failed);
    public int PendingCount => EnqueuedCount - CompletedCount - FailedCount;

    private sealed record WorkItem(
        Func<CancellationToken, Task> Work,
        string Description,
        DateTime EnqueuedAt);

    public BackgroundWorkQueue(int capacity = 64, ILogger<BackgroundWorkQueue>? logger = null)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _consumer = Task.Run(ProcessQueueAsync);
    }

    public void Enqueue(Func<CancellationToken, Task> work, string description)
    {
        Interlocked.Increment(ref _enqueued);
        if (!_channel.Writer.TryWrite(new WorkItem(work, description, DateTime.UtcNow)))
        {
            _logger?.LogWarning("BackgroundWorkQueue full, dropping work: {Description}", description);
        }
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(_cts.Token))
        {
            try
            {
                var age = DateTime.UtcNow - item.EnqueuedAt;
                if (age.TotalSeconds > 30)
                    _logger?.LogDebug("BackgroundWorkQueue: stale item ({Age:F0}s): {Description}",
                        age.TotalSeconds, item.Description);

                await item.Work(_cts.Token);
                Interlocked.Increment(ref _completed);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failed);
                _logger?.LogWarning(ex, "BackgroundWorkQueue: task failed: {Description}", item.Description);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();

        try { await _consumer; } catch { }

        _cts.Dispose();
        _logger?.LogInformation("BackgroundWorkQueue disposed: enqueued={E} completed={C} failed={F}",
            EnqueuedCount, CompletedCount, FailedCount);
    }
}
