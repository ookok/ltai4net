using System.Collections.Concurrent;
using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

    /// <summary>
    /// L1 Skill Evolution: MAF AIContextProvider that re-ranks tools by success-rate weighting.
    /// Runs first in the LTAI AIContextProvider chain to apply evolution-derived boosts.
    ///
    /// DeerFlow-inspired progressive loading: only injects full skill context when
    /// the task description matches the skill's advertised capability. Otherwise,
    /// only skill names are injected (advertise-only mode).
    /// </summary>
    public sealed class SkillRankingProvider : AIContextProvider
    {
        private readonly SkillEvolutionEngine _engine;
        private readonly ILogger<SkillRankingProvider> _logger;
        private static readonly HashSet<string> s_loadedSkills = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, double> s_skillRelevance = new(StringComparer.OrdinalIgnoreCase);

        // Skill metadata: name → description (advertise-only when not loaded)
        private static readonly Dictionary<string, string> s_skillCatalog = new(StringComparer.OrdinalIgnoreCase)
        {
            ["research"] = "Deep web research with multi-angle exploration and report generation",
            ["code"] = "Write, analyze, and refactor code across multiple languages",
            ["data"] = "Data analysis, visualization, and statistical modeling",
            ["writing"] = "Professional writing, editing, and content creation",
            ["diagram"] = "Generate diagrams, flowcharts, and architecture drawings",
            ["deploy"] = "Deployment automation, DevOps pipelines, and infrastructure",
            ["security"] = "Security audit, vulnerability analysis, and remediation",
            ["math"] = "Mathematical reasoning, proofs, and calculations",
        };

        public SkillRankingProvider(
            SkillEvolutionEngine engine,
            ILogger<SkillRankingProvider> logger) : base(null, null, null)
        {
            _engine = engine;
            _logger = logger;
        }

    // Override InvokingCoreAsync to REPLACE tools instead of concatenating.
    // MAF base class merges via a.Concat(b), which doubles the tool list.
#pragma warning disable MAAI001 // Experimental
    protected override async ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var inputContext = context.AIContext;

        var filteredContext = new InvokingContext(
            context.Agent,
            context.Session,
            new AIContext
            {
                Instructions = inputContext.Instructions,
                Messages = inputContext.Messages,
                Tools = inputContext.Tools
            });

        var provided = await ProvideAIContextAsync(filteredContext, cancellationToken).ConfigureAwait(false);

        var mergedInstructions = (inputContext.Instructions, provided.Instructions) switch
        {
            (null, null) => null,
            (string a, null) => a,
            (null, string b) => b,
            (string a, string b) => a + "\n" + b
        };

        var providedMessages = provided.Messages is not null
            ? provided.Messages.Select(m => m.WithAgentRequestMessageSource(
                AgentRequestMessageSourceType.AIContextProvider, GetType().FullName!))
            : null;

        var mergedMessages = (inputContext.Messages, providedMessages) switch
        {
            (null, null) => null,
            (var a, null) => a,
            (null, var b) => b,
            (var a, var b) => a.Concat(b)
        };

        // REPLACE tools: SkillRankingProvider re-orders the current set,
        // it should not double the list via base-class concatenation.
        var mergedTools = provided.Tools ?? inputContext.Tools;

        return new AIContext
        {
            Instructions = mergedInstructions,
            Messages = mergedMessages,
            Tools = mergedTools
        };
    }
#pragma warning restore MAAI001

    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken ct = default)
    {
        var existing = context.AIContext;
        if (existing?.Tools is null) return new ValueTask<AIContext>(existing!);

        var reRanked = existing.Tools
            .Select(t => (tool: t, boost: _engine.GetRankBoost(t.Name ?? "")))
            .OrderByDescending(x => x.boost)
            .Select(x => x.tool)
            .ToList();

        _logger.LogDebug("[SkillRanking] Re-ranked {Count} tools", reRanked.Count);

        // ── Progressive loading: build skill catalog injection ──
        var userQuery = context.AIContext?.Messages?.LastOrDefault(m =>
            m.Role == Microsoft.Extensions.AI.ChatRole.User)?.Text ?? "";

        var skillMessages = BuildSkillContext(userQuery);

        return new ValueTask<AIContext>(new AIContext
        {
            Tools = reRanked,
            Messages = skillMessages,
        });
    }

    /// <summary>
    /// DeerFlow-inspired progressive skill loading:
    /// - Injects full skill catalog (names + descriptions) always
    /// - Only loads full skill context when query matches a skill
    /// - Track loaded skills to avoid re-loading
    /// </summary>
    private List<Microsoft.Extensions.AI.ChatMessage>? BuildSkillContext(string userQuery)
    {
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>();
        if (string.IsNullOrEmpty(userQuery)) return null;

        // Always advertise available skills
        var catalog = string.Join("\n", s_skillCatalog.Select(kv => $"  - **{kv.Key}**: {kv.Value}"));
        if (!string.IsNullOrEmpty(catalog))
        {
            messages.Add(new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.System,
                $"## Available Skills\nActivate with /skill-name prefix.\n{catalog}"));
        }

        // Detect skill match from query
        var matchedSkill = DetectSkill(userQuery);
        if (matchedSkill != null && s_loadedSkills.Add(matchedSkill))
        {
            _logger.LogInformation("[SkillRanking] Progressively loaded skill: {Skill}", matchedSkill);
            messages.Add(new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.System,
                $"## Loaded Skill: {matchedSkill}\nFull skill context activated for this conversation."));
        }

        return messages.Count > 0 ? messages : null;
    }

    /// <summary>
    /// Simple keyword-based skill detection.
    /// Returns skill name if query matches, null otherwise.
    /// </summary>
    private static string? DetectSkill(string query)
    {
        if (string.IsNullOrEmpty(query)) return null;
        var lower = query.ToLowerInvariant();

        foreach (var (name, desc) in s_skillCatalog)
        {
            // Check for /skill-name prefix
            if (lower.StartsWith($"/{name}") || lower.StartsWith($"@{name}"))
                return name;

            // Check for keywords in the query
            var keywords = name switch
            {
                "research" => new[] { "research", "search", "find", "explore", "investigate", "report" },
                "code" => new[] { "code", "program", "function", "class", "implement", "refactor" },
                "data" => new[] { "data", "analyze", "statistics", "chart", "visualize" },
                "writing" => new[] { "write", "edit", "draft", "document", "blog", "article" },
                "diagram" => new[] { "diagram", "flowchart", "architecture", "draw" },
                "deploy" => new[] { "deploy", "ci/cd", "pipeline", "release" },
                "security" => new[] { "security", "vulnerability", "audit", "CVE" },
                "math" => new[] { "math", "calculate", "equation", "proof", "derivative" },
                _ => [],
            };

            if (keywords.Length > 0 && keywords.Any(k => lower.Contains(k)))
            {
                s_skillRelevance[name] = s_skillRelevance.GetValueOrDefault(name) + 0.1;
                if (s_skillRelevance[name] > 0.3)
                    return name;
            }
        }

        return null;
    }
}
