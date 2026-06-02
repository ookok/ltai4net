// Copyright (c) LTAI. All rights reserved.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Workflows.Declarative;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

/// <summary>
/// P15 hosted service that starts the <see cref="YAMLWorkflowWatcher"/> on
/// host start and stops it on host stop. The registry is loaded eagerly in
/// <see cref="StartAsync"/> so that the first request after process start
/// already has hot-editable config available.
/// </summary>
public sealed class WorkflowWatcherHostedService : IHostedService
{
    private readonly YAMLWorkflowRegistry _registry;
    private readonly YAMLWorkflowWatcher _watcher;
    private readonly IMcpToolHandler? _mcpToolHandler;
    private readonly ILogger<WorkflowWatcherHostedService> _logger;

    public WorkflowWatcherHostedService(
        YAMLWorkflowRegistry registry,
        YAMLWorkflowWatcher watcher,
        ILogger<WorkflowWatcherHostedService> logger,
        IMcpToolHandler? mcpToolHandler = null)
    {
        _registry = registry;
        _watcher = watcher;
        _mcpToolHandler = mcpToolHandler;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // P14.7: propagate the MCP handler to the static YAMLWorkflowHost
        // so its compiled greeting fast-path workflow can also resolve
        // InvokeMcpTool actions (no-op for greeting.yaml which never uses MCP).
        YAMLWorkflowHost.ConfigureMcpToolHandler(_mcpToolHandler);

        // Eagerly load every existing workflow file at startup so the
        // first user request doesn't pay the YAML compile cost.
        await _registry.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _watcher.Start();
        _logger.LogInformation("WorkflowWatcherHostedService started");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _watcher.Dispose();
        _logger.LogInformation("WorkflowWatcherHostedService stopped");
        return Task.CompletedTask;
    }
}
