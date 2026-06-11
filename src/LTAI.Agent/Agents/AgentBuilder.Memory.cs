#pragma warning disable MAAI001

using LTAI.Agent.Memory;
using LTAI.Agent.Indexing;
using LTAI.Agent.Tools;
using LTAI.AI;
using LTAI.AI.Compaction;
using LTAI.Core.Configuration;
using LTAI.Core.Safety;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

partial class AgentBuilder
{
    internal static CompactionProvider BuildCompactionProvider(IChatClient llm, IChatClient? steerLlm, LTAIOptions opts, ILoggerFactory loggerFactory)
    {
        return new CompactionProvider(
            new PipelineCompactionStrategy(
                new ContextWindowCompactionStrategy(opts.AI.ContextWindowSize, opts.AI.MaxTokens),
                new VerifiedSummarizationStrategy(
                    summarizer: llm,
                    verifier: steerLlm ?? llm,
                    trigger: CompactionTriggers.TokensExceed(opts.AI.ContextWindowSize),
                    minimumPreservedGroups: 2)
            ), loggerFactory: loggerFactory);
    }

    internal static PalaceStore BuildPalaceStore(EmbeddingClient embedder, LTAIOptions opts, ILoggerFactory loggerFactory)
    {
        var palaceDb = Path.Combine(opts.DataDirectory, "palace.db");
        WingClassifier.LlmClassifier = (text) => null;
        return new PalaceStore(embedder, palaceDb,
            loggerFactory.CreateLogger<PalaceStore>());
    }

    internal static string ResolveIdentity(LTAIOptions opts)
    {
        var identityPath = Path.Combine(AppContext.BaseDirectory, "identity.txt");
        var identityText = File.Exists(identityPath) ? File.ReadAllText(identityPath).Trim() : "";
        if (string.IsNullOrWhiteSpace(identityText))
            identityText = opts.AI.DefaultProvider ?? "";
        return identityText;
    }

    internal static List<string> ResolveSkillDirectories()
    {
        var apmSkillsDir = Path.Combine(Directory.GetCurrentDirectory(), ".agents", "skills");
        var skillsDir = new[] {
            Path.Combine(AppContext.BaseDirectory, "skills"),
            Path.Combine(Directory.GetCurrentDirectory(), "skills"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "skills"),
        }.FirstOrDefault(Directory.Exists) ?? Path.Combine(Directory.GetCurrentDirectory(), "skills");
        Directory.CreateDirectory(skillsDir);
        var skillDirs = new List<string> { skillsDir };
        if (Directory.Exists(apmSkillsDir))
            skillDirs.Add(apmSkillsDir);
        return skillDirs;
    }
}
