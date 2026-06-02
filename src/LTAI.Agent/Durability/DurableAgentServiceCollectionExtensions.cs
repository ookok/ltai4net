// Copyright (c) LTAI. All rights reserved.

using LTAI.Agent.Durability;
using LTAI.Core.Configuration;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DurableTask.Core;

namespace LTAI.Agent;

public static class DurableAgentServiceCollectionExtensions
{
    /// <summary>
    /// Wires up the MAF Durable Task pipeline for LTAI agents (P8 + P8.1).
    ///
    /// Architecture:
    ///   LTAI.Agent ──→ DurableAIAgentProxy ──→ IDurableAgentClient
    ///                                              │ (gRPC)
    ///                                              ▼
    ///                                     DurableTaskTestHost sidecar
    ///                                     (in-process, SQLiteOrchestrationService)
    ///
    /// Why: the legacy DTFx SDK's InProcessTestHost package provides a self-hostable
    /// gRPC sidecar that lives in the same process. MAF DurableTask's agent layer
    /// requires a DTFx gRPC backend; instead of running a sidecar process or going
    /// to Azure, we host the sidecar in-process.
    ///
    /// State scope: cross-restart persistent (SQLite). The orchestration instance
    /// store is snapshotted to a SQLite file on every write and rehydrated on
    /// startup, so an in-flight orchestration survives process restarts.
    /// </summary>
    public static IServiceCollection AddLTAIDurableAgents(this IServiceCollection services)
    {
        // Bind host options from LTAIOptions.Durable so user config (DatabasePath /
        // SidecarPort) flows through. The host is then constructed once DI is built.
        services.AddOptions<LTAIDurableAgentHostOptions>()
            .Configure<IOptions<LTAIOptions>>((opts, ltai) =>
            {
                opts.DatabasePath = ltai.Value.Durable.DatabasePath;
                opts.Port = ltai.Value.Durable.SidecarPort;
            });
        services.AddSingleton<LTAIDurableAgentHost>();
        services.AddHostedService(sp => sp.GetRequiredService<LTAIDurableAgentHost>());

        // Build a temp provider to resolve the host (so we know the port to pin the
        // gRPC sidecar to). The host we resolve here is the same singleton the
        // final container will hand out, so AddInMemoryDurableTask and the host
        // share the port. We don't dispose immediately because the host is a
        // singleton owned by the main provider; we just need its Port property.
        // The temp provider is disposed at the end — its only purpose is to read
        // the eager host instance.
        LTAIDurableAgentHost host;
        ServiceProvider tempProvider = services.BuildServiceProvider();
        try
        {
            host = tempProvider.GetRequiredService<LTAIDurableAgentHost>();
        }
        finally
        {
            (tempProvider as IDisposable)?.Dispose();
        }

        // Boot the in-process gRPC sidecar + DTFx worker + client (legacy SDK wrapper).
        // We pass the same port so the worker/client config targets our sidecar.
        // The empty registry is intentional: MAF's ConfigureDurableAgents below
        // adds the AgentEntity registration via its own AddTasks call (the
        // InProcessTestHost's worker is otherwise idle).
        services.AddInMemoryDurableTask(
            registry => { },
            new InMemoryDurableTaskOptions { Port = host.Port });

        // P8.1: swap the in-memory orchestration service for the SQLite-backed one.
        // AddInMemoryDurableTask registered 3 factory descriptors
        // (InMemoryOrchestrationService / IOrchestrationService / IOrchestrationServiceClient)
        // all pointing at the same InMemoryOrchestrationService instance. We mutate the
        // descriptor list in-place so subsequent DI resolutions construct
        // SQLiteOrchestrationService instead — DTFx gRPC and MAF code paths are unaware.
        // (Our service is registered as the concrete InMemoryOrchestrationService type
        // so the gRPC sidecar's GetRequiredService<InMemoryOrchestrationService> still
        // resolves to us — the sidecar doesn't care about the interface aliases.)
        var dbPath = host.DatabasePath;
        for (int i = services.Count - 1; i >= 0; i--)
        {
            var d = services[i];
            if (d.ServiceType == typeof(Microsoft.DurableTask.Testing.Sidecar.InMemoryOrchestrationService)
                || d.ServiceType == typeof(IOrchestrationService)
                || d.ServiceType == typeof(IOrchestrationServiceClient))
            {
                services[i] = new ServiceDescriptor(
                    d.ServiceType,
                    sp => (object)new SQLiteOrchestrationService(dbPath, sp.GetService<ILoggerFactory>()),
                    d.Lifetime);
            }
        }

        // Add MAF DI bits: IDurableAgentClient, DurableDataConverter, IWorkflowClient,
        // and register each agent factory as a DurableAIAgentProxy under its key.
        // ConfigureDurableAgents adds a SECOND DTFx worker (gRPC to our sidecar)
        // on top of InProcessTestHost's; that worker is the one that actually hosts
        // the agent entities.
        services.ConfigureDurableAgents(
            options =>
            {
                // The agent instances themselves are built lazily by the factories
                // registered in AddLTAIAgent. We add a stub factory that resolves
                // the keyed agent and lets MAF wrap it as a proxy.
                foreach (var def in GetDurableAgentNames(services))
                {
                    options.AddAIAgentFactory(def, sp =>
                        sp.GetRequiredKeyedService<Microsoft.Agents.AI.AIAgent>(def));
                }
            });

        return services;
    }

    /// <summary>
    /// Returns the LTAI agent names. Mirrors the registrations done in
    /// <see cref="ServiceCollectionExtensions.AddLTAIAgent(IServiceCollection, out IReadOnlyList{string})"/>
    /// without forcing a rebuild of the agent definitions.
    /// </summary>
    static IEnumerable<string> GetDurableAgentNames(IServiceCollection services)
    {
        // Read the names from any existing AgentDef loaders; we don't have direct
        // access to the static list, so we re-derive it from AgentRegistry.
        return LTAI.Agent.AgentRegistry.LoadAll()
            .Select(d => d.Name ?? "unknown")
            .Where(n => !string.IsNullOrEmpty(n));
    }
}
