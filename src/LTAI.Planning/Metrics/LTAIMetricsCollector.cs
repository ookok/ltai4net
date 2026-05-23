using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LTAI.Planning.Metrics;

public sealed class LTAIMetricsCollector : IDisposable
{
    private readonly Meter _meter;
    private readonly Counter<long> _requestCounter;
    private readonly Counter<long> _tokenCounter;
    private readonly Histogram<double> _latencyHistogram;
    private readonly Histogram<double> _tokenPerRequest;
    private readonly ObservableGauge<double> _dnaAwarenessGauge;
    private readonly ObservableGauge<double> _dnaFitnessGauge;
    private readonly ObservableGauge<long> _activeTasksGauge;
    private readonly ObservableGauge<long> _memoryMbGauge;

    private double _currentAwareness;
    private double _currentFitness;
    private long _currentActiveTasks;
    private long _totalRequests;
    private long _totalTokens;
    private double _totalLatencyMs;

    public LTAIMetricsCollector()
    {
        _meter = new Meter("LTAI", "7.0.0");

        _requestCounter = _meter.CreateCounter<long>("ltai_requests_total", description: "Total request count");
        _tokenCounter = _meter.CreateCounter<long>("ltai_tokens_total", description: "Total tokens processed");
        _latencyHistogram = _meter.CreateHistogram<double>("ltai_request_latency_ms", "ms", "Request latency");
        _tokenPerRequest = _meter.CreateHistogram<double>("ltai_tokens_per_request", description: "Tokens per request");
        _dnaAwarenessGauge = _meter.CreateObservableGauge("ltai_dna_awareness", () => _currentAwareness, description: "DNA awareness");
        _dnaFitnessGauge = _meter.CreateObservableGauge("ltai_dna_fitness", () => _currentFitness, description: "DNA fitness");
        _activeTasksGauge = _meter.CreateObservableGauge("ltai_active_tasks", () => _currentActiveTasks);
        _memoryMbGauge = _meter.CreateObservableGauge("ltai_memory_mb", () => Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024);
    }

    public void RecordRequest(double latencyMs, int inputTokens, int outputTokens)
    {
        _requestCounter.Add(1);
        var tokens = inputTokens + outputTokens;
        _tokenCounter.Add(tokens);
        _latencyHistogram.Record(latencyMs);
        _tokenPerRequest.Record(tokens);
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Add(ref _totalTokens, tokens);
        Interlocked.Exchange(ref _totalLatencyMs, _totalLatencyMs + latencyMs);
    }

    public void UpdateDNA(double awareness, double fitness, long activeTasks)
    {
        _currentAwareness = awareness;
        _currentFitness = fitness;
        _currentActiveTasks = activeTasks;
    }

    public LTAIMetricsSnapshot GetSnapshot() => new()
    {
        TotalRequests = _totalRequests,
        TotalTokens = _totalTokens,
        AvgLatencyMs = _totalRequests > 0 ? _totalLatencyMs / _totalRequests : 0,
        Awareness = _currentAwareness,
        Fitness = _currentFitness,
        ActiveTasks = _currentActiveTasks,
        MemoryMb = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024
    };

    public void Dispose() => _meter.Dispose();
}

public sealed class LTAIMetricsSnapshot
{
    public long TotalRequests { get; init; }
    public long TotalTokens { get; init; }
    public double AvgLatencyMs { get; init; }
    public double Awareness { get; init; }
    public double Fitness { get; init; }
    public long ActiveTasks { get; init; }
    public long MemoryMb { get; init; }
}
