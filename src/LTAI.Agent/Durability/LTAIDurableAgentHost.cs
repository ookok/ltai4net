// Copyright (c) LTAI. All rights reserved.

using System.Net;
using System.Net.Sockets;
using Microsoft.DurableTask.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Durability;

/// <summary>
/// In-process gRPC sidecar for the MAF Durable Task pipeline.
///
/// Why: MAF references DTFx out-of-process SDK 1.18+ which expects a gRPC sidecar
/// (cloud DTS, Azure Storage, or self-host binary). The Microsoft-owned
/// <c>Microsoft.DurableTask.InProcessTestHost 0.2.3-preview.1</c> package provides a
/// self-hostable gRPC sidecar that lives inside the same process via
/// <c>AddInMemoryDurableTask</c>. The package is preview-only and labelled "for
/// testing", but functionally it's a production-grade in-process DTFx sidecar backed
/// by an in-memory <c>IOrchestrationService</c>. We accept the preview dependency
/// per the B1 plan decision (self-host gRPC sidecar, no Azure / cloud).
///
/// Lifecycle: this host registers the in-process sidecar as an <c>IHostedService</c>
/// and pins a known loopback port so the MAF <c>ConfigureDurableAgents</c> worker /
/// client config can target it deterministically.
///
/// State: cross-restart persistence is a follow-up (write the in-memory orchestration
/// state to a SQLite snapshot file on shutdown / restore on startup).
/// </summary>
public sealed class LTAIDurableAgentHost : IHostedService
{
    readonly ILogger<LTAIDurableAgentHost> _logger;
    readonly LTAIDurableAgentHostOptions _options;

    public LTAIDurableAgentHost(
        LTAIDurableAgentHostOptions options,
        ILogger<LTAIDurableAgentHost> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Loopback address where the in-process gRPC sidecar listens. Valid only after
    /// <see cref="StartAsync"/> has completed.
    /// </summary>
    public string Endpoint { get; private set; } = string.Empty;

    public int Port { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Pre-allocate a free loopback port so the worker / client config can
        // bind to a known address. The listener is closed before the sidecar
        // binds so the address can actually be claimed.
        Port = _options.Port ?? ReserveLoopbackPort();
        Endpoint = $"http://localhost:{Port}";

        _logger.LogInformation(
            "LTAI Durable Agent sidecar reserved loopback port {Port} (endpoint={Endpoint})",
            Port, Endpoint);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

/// <summary>
/// Options for <see cref="LTAIDurableAgentHost"/>.
/// </summary>
public sealed class LTAIDurableAgentHostOptions
{
    /// <summary>
    /// Fixed loopback port for the gRPC sidecar. When <c>null</c>, a free port
    /// is allocated at startup. Pin the port only for debugging / tests.
    /// </summary>
    public int? Port { get; set; }
}
