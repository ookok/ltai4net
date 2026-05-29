using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.AI.Governors;

public interface IDataflowStage<TIn, TOut>
{
    string Name { get; }
    IAsyncEnumerable<TOut> ProcessAsync(IAsyncEnumerable<TIn> inputs, CancellationToken ct = default);
}

public sealed class DataflowPipeline
{
    private readonly List<object> _stages = new();
    private readonly ILogger<DataflowPipeline> _logger;

    public DataflowPipeline(ILogger<DataflowPipeline>? logger = null)
    {
        _logger = logger ?? NullLogger<DataflowPipeline>.Instance;
    }

    public DataflowPipeline AddStage<TIn, TOut>(IDataflowStage<TIn, TOut> stage)
    {
        _stages.Add(stage);
        return this;
    }

    public void Clear() => _stages.Clear();
    public int StageCount => _stages.Count;

    public async IAsyncEnumerable<TLast> ExecuteAsync<TFirst, TLast>(
        IAsyncEnumerable<TFirst> source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        object current = source;

        foreach (var stageObj in _stages)
        {
            var stageType = stageObj.GetType();
            var iface = stageType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDataflowStage<,>));

            if (iface == null)
            {
                _logger.LogWarning("Stage {Name} does not implement IDataflowStage<,>", stageType.Name);
                continue;
            }

            var method = iface.GetMethod("ProcessAsync");
            if (method == null)
            {
                _logger.LogWarning("Stage {Name} has no ProcessAsync method", stageType.Name);
                continue;
            }

            var stageName = stageType.GetProperty("Name")?.GetValue(stageObj)?.ToString() ?? stageType.Name;

            var taskObj = method.Invoke(stageObj, new object[] { current, ct });
            if (taskObj is not IAsyncEnumerable<object> typedResult)
            {
                _logger.LogError("Stage {Name} returned non-AsyncEnumerable", stageName);
                yield break;
            }

            if (current == source && _stages.Count == 1)
            {
                await foreach (var item in typedResult.WithCancellation(ct))
                    yield return (TLast)item;
                yield break;
            }

            var channel = Channel.CreateUnbounded<object>();
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var item in typedResult.WithCancellation(ct))
                        channel.Writer.TryWrite(item);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Stage {Name} errored", stageName);
                }
                finally
                {
                    channel.Writer.Complete();
                }
            }, ct);

            current = channel.Reader.ReadAllAsync(ct);
        }

        if (current is IAsyncEnumerable<object> final)
        {
            await foreach (var item in final.WithCancellation(ct))
                yield return (TLast)item;
        }
        else if (current is IAsyncEnumerable<TLast> directFinal)
        {
            await foreach (var item in directFinal.WithCancellation(ct))
                yield return item;
        }
    }

    public async IAsyncEnumerable<TOut> RunAsync<TIn, TOut>(
        IAsyncEnumerable<TIn> source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in ExecuteAsync<TIn, TOut>(source, ct))
            yield return item;
    }
}

public sealed class FuncDataflowStage<TIn, TOut> : IDataflowStage<TIn, TOut>
{
    private readonly Func<IAsyncEnumerable<TIn>, CancellationToken, IAsyncEnumerable<TOut>> _func;

    public FuncDataflowStage(string name, Func<IAsyncEnumerable<TIn>, CancellationToken, IAsyncEnumerable<TOut>> func)
    {
        Name = name;
        _func = func;
    }

    public string Name { get; }

    public IAsyncEnumerable<TOut> ProcessAsync(IAsyncEnumerable<TIn> inputs, CancellationToken ct = default)
        => _func(inputs, ct);
}

public sealed class FilterStage<T> : IDataflowStage<T, T>
{
    private readonly Func<T, bool> _predicate;

    public FilterStage(string name, Func<T, bool> predicate)
    {
        Name = name;
        _predicate = predicate;
    }

    public string Name { get; }

    public async IAsyncEnumerable<T> ProcessAsync(IAsyncEnumerable<T> inputs, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in inputs.WithCancellation(ct))
            if (_predicate(item))
                yield return item;
    }
}

public sealed class TransformStage<TIn, TOut> : IDataflowStage<TIn, TOut>
{
    private readonly Func<TIn, TOut> _transform;

    public TransformStage(string name, Func<TIn, TOut> transform)
    {
        Name = name;
        _transform = transform;
    }

    public string Name { get; }

    public async IAsyncEnumerable<TOut> ProcessAsync(IAsyncEnumerable<TIn> inputs, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in inputs.WithCancellation(ct))
            yield return _transform(item);
    }
}

public sealed class BatchStage<T> : IDataflowStage<T, T[]>
{
    private readonly int _batchSize;
    private readonly TimeSpan _maxWait;

    public BatchStage(string name, int batchSize = 32, TimeSpan? maxWait = null)
    {
        Name = name;
        _batchSize = batchSize;
        _maxWait = maxWait ?? TimeSpan.FromMilliseconds(500);
    }

    public string Name { get; }

    public async IAsyncEnumerable<T[]> ProcessAsync(IAsyncEnumerable<T> inputs, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var batch = new List<T>(_batchSize);
        var channel = Channel.CreateBounded<T>(_batchSize * 2);

        var fillTask = FillChannelAsync(inputs, channel, ct);

        while (!ct.IsCancellationRequested)
        {
            batch.Clear();
            using var timeoutCts = new CancellationTokenSource(_maxWait);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var timedOut = false;
            while (batch.Count < _batchSize && !timedOut)
            {
                try
                {
                    var readTask = channel.Reader.ReadAsync(linkedCts.Token).AsTask();
                    var completed = await Task.WhenAny(readTask, Task.Delay(_maxWait, ct)).ConfigureAwait(false);
                    if (completed != readTask)
                    {
                        timedOut = true;
                        break;
                    }
                    batch.Add(readTask.Result);
                }
                catch (OperationCanceledException)
                {
                    timedOut = true;
                }
                catch (ChannelClosedException)
                {
                    break;
                }
            }

            if (batch.Count > 0)
                yield return batch.ToArray();

            if (timedOut && batch.Count == 0)
                break;

            if (channel.Reader.Completion.IsCompleted && channel.Reader.Count == 0 && batch.Count == 0)
                break;
        }

        await fillTask.ConfigureAwait(false);
    }

    private static async Task FillChannelAsync(IAsyncEnumerable<T> inputs, Channel<T> channel, CancellationToken ct)
    {
        try
        {
            await foreach (var item in inputs.WithCancellation(ct))
                await channel.Writer.WriteAsync(item, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            channel.Writer.Complete();
        }
    }
}
