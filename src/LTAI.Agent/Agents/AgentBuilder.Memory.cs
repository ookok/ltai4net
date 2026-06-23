#pragma warning disable MAAI001

using LTAI.Agent.Memory;
using LTAI.Agent.Indexing;
using LTAI.Agent.Prompts;
using LTAI.Agent.Tools;
using LTAI.AI;
using LTAI.AI.Compaction;
using LTAI.Core.Configuration;
using LTAI.Core.I18n;
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
        // Derive compaction window from model's actual context window size.
        // Fall back to 32K when model is unknown to avoid compressing too aggressively.
        var modelWindow = UsageTracker.ResolveContextWindow(opts.AI.Model ?? "", 32768);
        var compactionWindow = Math.Min(modelWindow, 32768); // don't exceed 32K even for 1M+ models
        var windowStrategy = new ContextWindowCompactionStrategy(compactionWindow, opts.AI.MaxTokens);
        var summaryTrigger = CompactionTriggers.TokensExceed(compactionWindow * 6 / 10);

        var lang = Locale.IsChinese ? "zh" : "en";
        var compactionPrompt = PromptLoader.Load($"compaction-{lang}");
        if (string.IsNullOrWhiteSpace(compactionPrompt))
            compactionPrompt = VerifiedSummarizationStrategy.DefaultSummarizationPrompt;

        return new CompactionProvider(
            new PipelineCompactionStrategy(
                windowStrategy,
                new VerifiedSummarizationStrategy(
                    summarizer: llm,
                    verifier: steerLlm ?? llm,
                    trigger: summaryTrigger,
                    minimumPreservedGroups: 2,
                    summarizationPrompt: compactionPrompt)
            ), loggerFactory: loggerFactory);
    }

    internal static PalaceStore BuildPalaceStore(EmbeddingClient embedder, LTAIOptions opts, ILoggerFactory loggerFactory)
    {
        // Use kg.db as the shared palace store (was palace.db before Phase 1.1)
        var palaceDb = opts.ResolveDataPath("kg.db");
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
