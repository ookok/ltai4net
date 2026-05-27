using LTAI.Knowledge.Core;
using LTAI.Core.Interfaces;
using LTAI.Core.Providers;
using LTAI.Core.Governors;
using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public static class ContextHubBuilder
{
    public static ContextHub Build(
        DualMemoryStore? dualMemory,
        MemoryFilesService? memoryFiles,
        KnowledgeGraph? knowledgeGraph,
        Func<string, int, List<ContextItem>>? skillStoreQuery = null,
        ICrossRunEvolutionStore? evolutionStore = null,
        HarnessEvolution? harnessEvo = null,
        ContextMapStore? contextMap = null,
        SynapticMemory? synapticMemory = null,
        ContextGovernor? contextGovernor = null,
        TextRetrievalBooster? booster = null,
        DualRouteRetriever? dualRouteRetriever = null,
        ILogger<ContextHub>? logger = null)
    {
        var hub = new ContextHub(logger);

        if (dualRouteRetriever != null)
        {
            hub.RegisterDualRouteMemory(dualRouteRetriever);
        }

        if (dualMemory != null)
        {
            hub.RegisterStore(ContextDomain.Memory, (query, topK) =>
            {
                try
                {
                    var episodes = dualMemory.FindSimilarEpisodes(query, limit: topK);
                    return episodes.Select(e => new ContextItem
                    {
                        Domain = ContextDomain.Memory,
                        Kind = ContextKind.Episode,
                        Summary = e.Query[..Math.Min(e.Query.Length, 80)],
                        Detail = e.FinalAnswer?[..Math.Min(e.FinalAnswer.Length, 200)],
                        Relevance = e.Confidence,
                        Confidence = e.Reward,
                        Timestamp = e.Timestamp,
                        Links = new() { ["domain"] = e.Domain }
                    }).ToList();
                }
                catch { return new(); }
            });
        }

        if (memoryFiles != null)
        {
            hub.RegisterStore(ContextDomain.Knowledge, (query, topK) =>
            {
                try
                {
                    var files = memoryFiles.RetrieveRelevant(query, topK: topK);
                    return files.Select(f => new ContextItem
                    {
                        Domain = ContextDomain.Knowledge,
                        Kind = ContextKind.Entity,
                        Summary = f.Name,
                        Detail = f.Summary,
                        Relevance = (float)f.Confidence,
                        Confidence = (float)f.Confidence,
                        Links = new() { ["domain"] = f.Domain }
                    }).ToList();
                }
                catch { return new(); }
            });
        }

        if (knowledgeGraph != null)
        {
            hub.RegisterStore(ContextDomain.Knowledge, (query, topK) =>
            {
                try
                {
                    var entities = knowledgeGraph.SearchEntities(query, topK);
                    return entities.Select(e => new ContextItem
                    {
                        Domain = ContextDomain.Knowledge,
                        Kind = ContextKind.Entity,
                        Summary = e.Label,
                        Detail = e.Id,
                        Relevance = 0.6f,
                        Confidence = 0.6f,
                        Links = new() { ["entityId"] = e.Id }
                    }).ToList();
                }
                catch { return new(); }
            });
        }

        if (skillStoreQuery != null)
        {
            hub.RegisterStore(ContextDomain.Skill, skillStoreQuery);
        }

        if (evolutionStore != null)
        {
            hub.RegisterStore(ContextDomain.Evolution, (query, topK) =>
            {
                try
                {
                    var lessons = evolutionStore.GetActiveLessons(topK);
                    return lessons.Select(l => new ContextItem
                    {
                        Domain = ContextDomain.Evolution,
                        Kind = ContextKind.Lesson,
                        Summary = l.Summary ?? "",
                        Relevance = l.Severity,
                        Confidence = l.Severity
                    }).ToList();
                }
                catch { return new(); }
            });
        }

        if (harnessEvo != null)
        {
            hub.RegisterStore(ContextDomain.Harness, (query, topK) =>
            {
                try
                {
                    var interventions = harnessEvo.Interventions.Values
                        .Where(i => i.IsActive)
                        .OrderByDescending(i => i.Effectiveness)
                        .Take(topK).ToList();
                    return interventions.Select(i => new ContextItem
                    {
                        Domain = ContextDomain.Harness,
                        Kind = ContextKind.Intervention,
                        Summary = $"[{i.Type}] {i.TriggerPattern}",
                        Detail = i.Action,
                        Relevance = i.Effectiveness,
                        Confidence = i.Confidence,
                        UseCount = i.SuccessCount
                    }).ToList();
                }
                catch { return new(); }
            });
        }

        if (contextMap != null)
        {
            hub.RegisterStore(ContextDomain.Map, (query, topK) =>
            {
                try
                {
                    var entries = contextMap.Entries.Values
                        .OrderByDescending(e => e.Priority)
                        .Take(topK).ToList();
                    return entries.Select(e => new ContextItem
                    {
                        Domain = ContextDomain.Map,
                        Kind = ContextKind.MapEntry,
                        Summary = $"{e.Key}: {e.Value}",
                        Relevance = (float)e.Priority,
                        Confidence = e.Confidence,
                        UseCount = e.UseCount,
                        Timestamp = e.LastUsed
                    }).ToList();
                }
                catch { return new(); }
            });
        }

        if (synapticMemory != null)
        {
            hub.RegisterStore(ContextDomain.Synaptic, (query, topK) =>
            {
                try
                {
                    var samples = synapticMemory.GetTrainingSamples(maxCount: topK);
                    return samples.Select(s => new ContextItem
                    {
                        Domain = ContextDomain.Synaptic,
                        Kind = ContextKind.Synapse,
                        Summary = s.Text[..Math.Min(s.Text.Length, 80)],
                        Detail = s.Label,
                        Relevance = s.Weight,
                        Confidence = s.Weight,
                        Links = new() { ["label"] = s.Label }
                    }).ToList();
                }
                catch { return new(); }
            });
        }

        if (contextGovernor != null)
        {
            hub.RegisterStore(ContextDomain.Conversation, (query, topK) =>
            {
                try
                {
                    var history = contextGovernor.CompressHistory();
                    if (string.IsNullOrWhiteSpace(history)) return new();
                    var turns = history.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Where(l => l.Contains(query, StringComparison.OrdinalIgnoreCase))
                        .Take(topK).ToList();
                    return turns.Select(t => new ContextItem
                    {
                        Domain = ContextDomain.Conversation,
                        Kind = ContextKind.ConversationTurn,
                        Summary = t[..Math.Min(t.Length, 80)],
                        Relevance = 0.6f,
                        Confidence = 0.5f
                    }).ToList();
                }
                catch { return new(); }
            });
        }

        return hub;
    }
}
