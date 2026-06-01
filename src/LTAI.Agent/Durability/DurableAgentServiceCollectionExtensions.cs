// Copyright (c) LTAI. All rights reserved.

using LTAI.Agent.Durability;
using LTAI.Core.Configuration;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.DurableTask.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace LTAI.Agent;

public static class DurableAgentServiceCollectionExtensions
{
    /// <summary>
    /// Wires up the MAF Durable Task pipeline for LTAI agents (P8).
    ///
    /// Architecture:
    ///   LTAI.Agent ──→ DurableAIAgentProxy ──→ IDurableAgentClient
    ///                                              │ (gRPC)
    ///                                              ▼
    ///                                     DurableTaskTestHost sidecar
    ///                                     (in-process, InMemoryOrchestrationService)
    ///
    /// Why: the legacy DTFx SDK's InProcessTestHost package provides a self-hostable
    /// gRPC sidecar that lives in the same process. MAF DurableTask's agent layer
    /// requires a DTFx gRPC backend; instead of running a sidecar process or going
    /// to Azure, we host the sidecar in-process.
    ///
    /// State scope: process-lifetime (in-memory). Cross-restart persistence is a
    /// later sub-step (snapshot orchestration state to SQLite on shutdown / restore
    /// on startup).
    /// </summary>
    public static IServiceCollection AddLTAIDurableAgents(this IServiceCollection services)
    {
        // Reserve a free loopback port now (constructor) so AddInMemoryDurableTask
        // can pin the same port for its sidecar/worker/client config.
        var hostOptions = new LTAIDurableAgentHostOptions();
        var durableHost = new LTAIDurableAgentHost(hostOptions, logger: null!);
        services.AddSingleton(durableHost);
        services.AddHostedService(sp => sp.GetRequiredService<LTAIDurableAgentHost>());

        // Boot the in-process gRPC sidecar + DTFx worker + client (legacy SDK wrapper).
        // We pass the same port so the worker/client config targets our sidecar.
        // The empty registry is intentional: MAF's ConfigureDurableAgents below
        // adds the AgentEntity registration via its own AddTasks call (the
        // InProcessTestHost's worker is otherwise idle).
        services.AddInMemoryDurableTask(
            registry => { },
            new InMemoryDurableTaskOptions { Port = durableHost.Port });

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
