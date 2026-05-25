using System.Diagnostics;
using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using LTAI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class SelfGovernor : LayerGovernor
{
    private readonly List<Activity> _traces = new();
    private readonly Random _rng = new();

    public SelfGovernor(ICognitiveMesh mesh, IChatClient llm, ILogger<SelfGovernor> logger)
        : base("self", mesh, llm, logger) { }

    public override Task<Handshake> ProcessAsync(Handshake incoming, CancellationToken cancellationToken = default)
    {
        return incoming.Action switch
        {
            "health_check" => HandleHealthCheck(),
            "start_trace" => HandleStartTrace(incoming),
            "inject_chaos" => HandleChaosInjection(),
            _ => Task.FromResult(new Handshake { From = LayerName, Action = "self_ack" })
        };
    }

    private Task<Handshake> HandleHealthCheck()
    {
        var memMb = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;
        var cpuUsage = Environment.ProcessorCount;

        var healthy = memMb < 4096;
        var mode = healthy ? SystemMode.Normal : SystemMode.Degraded;

        return Task.FromResult(new Handshake
        {
            From = LayerName,
            Action = "vitals",
            Payload = new Dictionary<string, object?>
            {
                ["memory_mb"] = memMb,
                ["cpu_cores"] = cpuUsage,
                ["healthy"] = healthy,
                ["mode"] = mode.ToString()
            }
        });
    }

    private Task<Handshake> HandleStartTrace(Handshake incoming)
    {
        var traceId = incoming.Payload?.GetValueOrDefault("trace_id")?.ToString() ?? Guid.NewGuid().ToString("N");
        var activity = new Activity("ltai_trace");
        activity.SetParentId(traceId);
        activity.Start();
        _traces.Add(activity);
        Logger.LogInformation("Trace started: {TraceId}", traceId);

        return Task.FromResult(new Handshake
        {
            From = LayerName,
            Action = "trace_started",
            Payload = new Dictionary<string, object?> { ["trace_id"] = traceId }
        });
    }

    private Task<Handshake> HandleChaosInjection()
    {
        var targets = new[] { "input", "context", "routing", "capability", "storage", "output", "communication", "task" };
        var target = targets[_rng.Next(targets.Length)];

        Logger.LogWarning("Chaos injected into: {Target}", target);
        return Task.FromResult(new Handshake
        {
            From = LayerName,
            Action = "chaos_injected",
            Payload = new Dictionary<string, object?> { ["target"] = target }
        });
    }
}
