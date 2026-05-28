using LTAI.Core.Governors;
using LTAI.Core.Interfaces;
using LTAI.Core.System;
using LTAI.DNA.Safety;
using LTAI.Knowledge.Core;
using LTAI.Models;
using LTAI.Tools.GIS;
using HarnessProfile = LTAI.Core.Configuration.HarnessProfile;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LTAI.Agent.Adversarial;
using LTAI.Agent.Agents;
using LTAI.Agent.Feedback;
using LTAI.Agent.Skills;
using LTAI.Agent.Skills.Runtime;
using LTAI.Agent.Workflows;
using LTAI.Agent.Federation;
using LTAI.Agent.MAF;
using LTAI.Agent.Middleware;
using LTAI.Agent.Prefetch;
using LTAI.Agent.Prompting;
using LTAI.Agent.Routing;
using LTAI.Agent.Tools;
using LTAI.Core.Configuration;

namespace LTAI.Agent;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIAgent(this IServiceCollection services)
    {
        services.AddHttpClient("SkillPublisher");
        services.AddSingleton<Skills.SkillRegistry>();
        services.AddSingleton<SkillLoader>();
        services.AddSingleton<SkillInstaller>();
        services.AddSingleton<SkillPublisher>(sp =>
        {
            var registry = sp.GetRequiredService<Skills.SkillRegistry>();
            var loader = sp.GetRequiredService<Skills.SkillLoader>();
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var http = httpFactory.CreateClient("SkillPublisher");
            var logger = sp.GetRequiredService<ILogger<SkillPublisher>>();
            return new SkillPublisher(registry, loader, http, logger);
        });
        services.AddSingleton<ISkillExchangeProvider>(sp => sp.GetRequiredService<SkillPublisher>());
        services.AddHostedService<SkillSyncService>();
        services.AddSingleton<MarketplaceClient>(sp =>
        {
            var httpClientFactory = sp.GetService<IHttpClientFactory>();
            var http = httpClientFactory?.CreateClient("Marketplace") ?? new HttpClient();
            var logger = sp.GetRequiredService<ILogger<MarketplaceClient>>();
            var baseUrl = OptionService.Get("LTAI_MARKETPLACE_URL") ?? Environment.GetEnvironmentVariable("LTAI_MARKETPLACE_URL");
            return new MarketplaceClient(http, logger, baseUrl);
        });
        services.AddSingleton<SkillExtractor>();
        services.AddSingleton<SkillRuntime>(sp =>
        {
            var registry = sp.GetRequiredService<Skills.SkillRegistry>();
            var logger = sp.GetService<ILogger<SkillRuntime>>();
            var safetyGate = sp.GetRequiredService<UnifiedSafetyGate>();
            return new SkillRuntime(registry, logger,
                externalSafetyGate: (tool, input) => safetyGate.EvaluateToolCall(tool, input));
        });
        services.AddSingleton<SkillAwareDecomposer>();
#pragma warning disable CS0618 // IntentRouter is [Obsolete] but still needed for agent-level routing
        services.AddSingleton<IntentRouter>();
#pragma warning restore CS0618
        services.AddSingleton<UnifiedSemanticRouter>();
        services.AddSingleton<UnifiedIntentRouter>();
        services.AddSingleton<SpatialCache>();
        services.AddSingleton<UniversalOrchestrator>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<UniversalOrchestrator>>();
            var router = sp.GetRequiredService<UnifiedSemanticRouter>();
            var harness = sp.GetService<HarnessProfile>();
            var concurrencyGuard = sp.GetService<IConcurrencyGuard>();
            return new UniversalOrchestrator(logger, router, harness,
                concurrencyGuard: concurrencyGuard);
        });
        services.AddSingleton<ToolRetriever>();
        services.AddSingleton<PlannerCriticWorkflow>();
        services.AddSingleton<PlannerIntegration>();
        services.AddSingleton<UnifiedPlanningPipeline>();
        services.AddSingleton<TaskQueue>();
        services.AddSingleton<LTAICoordinator>();

        services.AddSingleton<GitWorktreeManager>(sp =>
        {
            var workspace = OptionService.Get("LTAI_WORKSPACE") ?? Directory.GetCurrentDirectory();
            var logger = sp.GetService<ILogger<GitWorktreeManager>>();
            return new GitWorktreeManager(workspace, null, logger);
        });

        services.AddSingleton<AgentWorktreeSession>(sp =>
        {
            var worktreeManager = sp.GetRequiredService<GitWorktreeManager>();
            var lts = sp.GetRequiredService<LTAI.AI.Interfaces.ILivingTreeSystem>();
            var logger = sp.GetService<ILogger<AgentWorktreeSession>>();
            return new AgentWorktreeSession(worktreeManager, lts, logger);
        });

        services.AddSingleton<MergeConflictResolver>(sp =>
        {
            var worktreeManager = sp.GetRequiredService<GitWorktreeManager>();
            var logger = sp.GetService<ILogger<MergeConflictResolver>>();
            return new MergeConflictResolver(worktreeManager, logger);
        });

        services.AddSingleton<WorktreeOrchestrator>(sp =>
        {
            var coordinator = sp.GetRequiredService<LTAICoordinator>();
            var worktreeManager = sp.GetRequiredService<GitWorktreeManager>();
            var sessionManager = sp.GetRequiredService<AgentWorktreeSession>();
            var lts = sp.GetRequiredService<LTAI.AI.Interfaces.ILivingTreeSystem>();
            var promptService = sp.GetService<PromptService>();
            var logger = sp.GetService<ILogger<WorktreeOrchestrator>>();
            var backpressure = sp.GetService<BackpressurePipeline>();
            var config = new WorktreeOrchestratorConfig
            {
                EnableWorktreeIsolation = true,
                BaseBranch = "main",
                MaxConcurrency = 4,
                AutoCommit = true,
                AutoPruneOnCompletion = true,
                StaleThreshold = TimeSpan.FromHours(24)
            };
            return new WorktreeOrchestrator(coordinator, worktreeManager, sessionManager,
                lts, promptService, config, backpressure, logger);
        });

        services.AddSingleton<IProjectSpecProvider>(sp =>
        {
            var workspace = OptionService.Get("LTAI_WORKSPACE") ?? Directory.GetCurrentDirectory();
            var logger = sp.GetService<ILogger<ProjectDetector>>();
            var detector = new ProjectDetector(workspace, logger);
            var spec = detector.Detect();
            return new ProjectSpecProvider(spec);
        });

        services.AddSingleton<LintGate>(sp =>
        {
            var kernel = sp.GetService<IMicroKernel>();
            var projectSpec = sp.GetService<IProjectSpecProvider>();
            var logger = sp.GetService<ILogger<LintGate>>();
            return new LintGate(kernel, logger, projectSpec);
        });
        services.AddSingleton<TypecheckGate>(sp =>
        {
            var kernel = sp.GetService<IMicroKernel>();
            var projectSpec = sp.GetService<IProjectSpecProvider>();
            var logger = sp.GetService<ILogger<TypecheckGate>>();
            return new TypecheckGate(kernel, logger, projectSpec);
        });
        services.AddSingleton<TestGate>(sp =>
        {
            var kernel = sp.GetService<IMicroKernel>();
            var projectSpec = sp.GetService<IProjectSpecProvider>();
            var logger = sp.GetService<ILogger<TestGate>>();
            return new TestGate(kernel, logger, projectSpec);
        });
        services.AddSingleton<ReviewGate>(sp =>
        {
            var lts = sp.GetService<LTAI.AI.Interfaces.ILivingTreeSystem>();
            var logger = sp.GetService<ILogger<ReviewGate>>();
            return new ReviewGate(lts, logger);
        });

        services.AddSingleton<BackpressurePipeline>(sp =>
        {
            var gates = new IBackpressureGate[]
            {
                sp.GetRequiredService<LintGate>(),
                sp.GetRequiredService<TypecheckGate>(),
                sp.GetRequiredService<TestGate>(),
                sp.GetRequiredService<ReviewGate>()
            };
            var logger = sp.GetService<ILogger<BackpressurePipeline>>();
            return new BackpressurePipeline(gates, new BackpressurePipelineConfig
            {
                MaxAttempts = 3,
                FailFast = true
            }, logger);
        });

        services.AddHostedService<WorktreeCleanupService>(sp =>
        {
            var worktreeManager = sp.GetRequiredService<GitWorktreeManager>();
            var logger = sp.GetService<ILogger<WorktreeCleanupService>>();
            var staleThreshold = TimeSpan.FromHours(24);
            var checkInterval = TimeSpan.FromMinutes(30);
            return new WorktreeCleanupService(worktreeManager, staleThreshold, checkInterval, logger);
        });

        services.AddSingleton<GitExperimentBridge>(sp =>
        {
            var worktreeManager = sp.GetRequiredService<GitWorktreeManager>();
            var logger = sp.GetService<ILogger<GitExperimentBridge>>();
            return new GitExperimentBridge(worktreeManager, logger);
        });

        services.AddSingleton<Skills.SkillGraph>(sp =>
        {
            var logger = sp.GetService<ILogger<Skills.SkillGraph>>();
            return new Skills.SkillGraph(logger);
        });

        services.AddSingleton<Skills.SkillGraphEvolver>(sp =>
        {
            var graph = sp.GetRequiredService<Skills.SkillGraph>();
            var registry = sp.GetRequiredService<Skills.SkillRegistry>();
            var logger = sp.GetService<ILogger<Skills.SkillGraphEvolver>>();
            return new Skills.SkillGraphEvolver(graph, registry, logger);
        });

        services.AddSingleton<SubgraphTaskDecomposer>(sp =>
        {
            var graph = sp.GetRequiredService<Skills.SkillGraph>();
            var logger = sp.GetService<ILogger<SubgraphTaskDecomposer>>();
            return new SubgraphTaskDecomposer(graph, logger);
        });

        services.AddSingleton<SkillGraphMaintainer>(sp =>
        {
            var graph = sp.GetRequiredService<Skills.SkillGraph>();
            var logger = sp.GetService<ILogger<SkillGraphMaintainer>>();
            return new SkillGraphMaintainer(graph, logger);
        });

        services.AddSingleton<SkillGraphMarkdownBridge>(sp =>
        {
            var graph = sp.GetRequiredService<Skills.SkillGraph>();
            var skillsRoot = OptionService.Get("paths.skills");
            return new SkillGraphMarkdownBridge(graph, skillsRoot);
        });

        services.AddSingleton<AdversarialSelfPlay>(sp =>
        {
            var instance = AdversarialSelfPlay.Instance;
            instance.SetLogger(sp.GetRequiredService<ILogger<AdversarialSelfPlay>>());
            return instance;
        });
        services.AddSingleton<SelfRefinementLoop>();
        services.AddSingleton<AgentParliament>();

        // Register BuiltInHooks on AgentHookPipeline
        services.AddSingleton<AgentHookPipeline>(sp =>
        {
            var pipeline = new AgentHookPipeline();
            pipeline.OnPreToolUse(BuiltInHooks.ShellSafetyHook);
            pipeline.OnPreToolUse(BuiltInHooks.FileSystemSafetyHook);
            pipeline.OnPreToolUse(BuiltInHooks.NetworkSafetyHook);
            return pipeline;
        });
        services.AddSingleton<SentientParliament>();
        services.AddSingleton<ShadowRouter>();
        services.AddSingleton<HumanInTheLoopReview>();
        services.AddSingleton<FeedbackCollector>();
        services.AddSingleton<ABExperimentEngine>();
        services.AddSingleton<FederationCoordinator>();
        services.AddSingleton<BudgetTrackingMiddleware>();
        services.AddSingleton<AgentRegistry>();
        services.AddSingleton<AgentRegistryLock>();
        services.AddSingleton<IAgentFactory, AgentFactory>();
        services.AddSingleton<PredictivePrefetcher>();

        // ProAct: idle-time anticipation (arXiv:2605.25971)
        services.AddSingleton<ProActAnticipator>(sp =>
        {
            var memoryGraph = sp.GetRequiredService<MemoryGraph>();
            var cps = sp.GetService<ICPSProcessingService>();
            var memPi = sp.GetService<IMemPiGuidance>();
            var logger = sp.GetService<ILogger<ProActAnticipator>>();
            return new ProActAnticipator(memoryGraph, cps, memPi, config: null, logger);
        });
        services.AddSingleton<IProActCache>(sp => sp.GetRequiredService<ProActAnticipator>());

        services.AddSingleton<Agents.CodeAgentAdapter>();
        services.AddSingleton<Agents.EIAAgentAdapter>();
        services.AddSingleton<Agents.ChatAgentAdapter>();
        services.AddSingleton<Agents.ReasoningAgentAdapter>();

        services.AddSingleton<Agents.CodeAgentFactory>();
        services.AddSingleton<Agents.EIAAgentFactory>();
        services.AddSingleton<Agents.ChatAgentFactory>();
        services.AddSingleton<Agents.ReasoningAgentFactory>();

        services.AddSingleton<LTAI.Core.Interfaces.IAgentFactory>(sp => sp.GetRequiredService<Agents.CodeAgentFactory>());
        services.AddSingleton<LTAI.Core.Interfaces.IAgentFactory>(sp => sp.GetRequiredService<Agents.EIAAgentFactory>());
        services.AddSingleton<LTAI.Core.Interfaces.IAgentFactory>(sp => sp.GetRequiredService<Agents.ChatAgentFactory>());
        services.AddSingleton<LTAI.Core.Interfaces.IAgentFactory>(sp => sp.GetRequiredService<Agents.ReasoningAgentFactory>());
        services.AddHostedService<ReflectiveIdlingService>();
        services.AddHostedService<StartupRecoveryService>();

        services.AddSingleton<IMicroKernel>(sp =>
        {
            var workspace = OptionService.Get("LTAI_WORKSPACE") ?? Directory.GetCurrentDirectory();
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Default");
            var logger = sp.GetService<ILogger<MicroKernel>>();

            Func<string, string, CancellationToken, Task<string>>? gitHandler = null;
            try
            {
                var gitMgr = sp.GetService<GitWorktreeManager>();
                if (gitMgr != null)
                {
                    gitHandler = async (opCode, args, ct) =>
                    {
                        return opCode switch
                        {
                            "status" => string.Join("\n", await gitMgr.GetModifiedFilesAsync(args, ct)),
                            "diff" => await gitMgr.GetDiffAsync(args, ct),
                            "branch" => await gitMgr.GetCurrentBranchAsync(args, ct),
                            _ => $"Unsupported git op: {opCode}"
                        };
                    };
                }
            }
            catch { }

            Func<string, string, CancellationToken, Task<string>>? skillHandler = null;
            try
            {
                var skillRegistry = sp.GetService<Skills.SkillRegistry>();
                var skillRuntime = sp.GetService<SkillRuntime>();
                if (skillRegistry != null && skillRuntime != null)
                {
                    skillHandler = async (skillName, input, ct) =>
                    {
                        var skill = skillRegistry.Get(skillName);
                        if (skill == null) return $"Skill '{skillName}' not found";
                        skillRuntime.InjectContext(input, "general", "microkernel");
                        var result = await skillRuntime.RunAsync(skill, ct);
                        return result.Output;
                    };
                }
            }
            catch { }

            Func<string, int, CancellationToken, Task<string>>? memoryHandler = null;
            try
            {
                var kb = sp.GetService<LTAI.Knowledge.Core.KnowledgeBase>();
                var memGraph = sp.GetService<MemoryGraph>();
                if (kb != null || memGraph != null)
                {
                    memoryHandler = async (query, topK, ct) =>
                    {
                        var parts = new List<string>();

                        if (kb != null)
                        {
                            try
                            {
                                var results = await kb.SearchAsync(query, topK).ConfigureAwait(false);
                                if (results.Count > 0)
                                {
                                    parts.Add("[KnowledgeBase]");
                                    parts.AddRange(results.Select(r =>
                                        $"[{r.Domain}] {r.Title} (score={r.Score:F2}): {r.Content[..Math.Min(r.Content.Length, 300)]}"));
                                }
                            }
                            catch { }
                        }

                        if (memGraph != null)
                        {
                            try
                            {
                                var graphResults = memGraph.Search(query, topK);
                                if (graphResults.Count > 0)
                                {
                                    parts.Add("[MemoryGraph]");
                                    parts.AddRange(graphResults.Select(n =>
                                        $"[{n.Domain}] L{n.LayerLevel} {n.Summary[..Math.Min(n.Summary.Length, 200)]} (importance={n.Importance:F2})"));
                                }
                            }
                            catch { }
                        }

                        return parts.Count == 0
                            ? "No matching knowledge found."
                            : string.Join("\n---\n", parts);
                    };
                }
            }
            catch { }

            var sandboxConfig = KernelSandboxConfig.DevelopmentDefaults(workspace);
            var diffAgent = sp.GetService<SemanticDiffAgent>();

            var kernel = new MicroKernel(workspace, http,
                gitOpHandler: gitHandler,
                skillHandler: skillHandler,
                memoryHandler: memoryHandler,
                sandboxConfig: sandboxConfig,
                diffAgent: diffAgent,
                logger: logger);

            kernel.GenePool = sp.GetService<GenePool>();
            kernel.Teacher = sp.GetService<BootstrapTeacher>();

            foreach (var niche in new[] { "code", "eia", "chat", "reasoning" })
                kernel.SetNicheSandbox(niche, KernelSandboxConfig.NicheIsolation(workspace, niche));

            MicroKernel.Default = kernel;
            return kernel;
        });

        services.AddSingleton<ParetoRouter>(sp =>
        {
            var logger = sp.GetService<ILogger<ParetoRouter>>();
            var genePool = sp.GetService<GenePool>();
            return new ParetoRouter(embeddingDim: 768, metric: ParetoDistanceMetric.Cosine, logger: logger, genePool: genePool);
        });

        services.AddSingleton<BootstrapTeacher>(sp =>
        {
            var router = sp.GetRequiredService<ParetoRouter>();
            var logger = sp.GetService<ILogger<BootstrapTeacher>>();
            var thresholdsDir = Path.Combine(AppContext.BaseDirectory, "rules");
            var teacher = new BootstrapTeacher(router, thresholdsDir, logger);
            var scheduler = sp.GetRequiredService<CoordinationScheduler>();
            teacher.CoordinationPublisher = scheduler.Publish;
            return teacher;
        });

        services.AddSingleton<GenePool>(sp =>
        {
            var logger = sp.GetService<ILogger<GenePool>>();
            return new GenePool(config: new GenePoolConfig { MaxPopulation = 200 }, logger: logger);
        });

        services.AddSingleton<SimulatedAnnealer>(sp =>
        {
            var genePool = sp.GetRequiredService<GenePool>();
            var router = sp.GetRequiredService<ParetoRouter>();
            var logger = sp.GetService<ILogger<SimulatedAnnealer>>();
            // 延迟解析 ILivingTreeSystem 以打破循环依赖
            // SimulatedAnnealer → ILivingTreeSystem → CPSProcessingService → SimulatedAnnealer
            var serviceProvider = sp;
            Func<string, CancellationToken, Task<string>> l1Eval = async (query, ct) =>
            {
                var lts = serviceProvider.GetRequiredService<LTAI.AI.Interfaces.ILivingTreeSystem>();
                return await lts.ChatAsync(query, ct).ConfigureAwait(false);
            };
            return new SimulatedAnnealer(genePool, router, l1Eval, logger: logger);
        });

        services.AddSingleton<GeneToRule>(sp =>
        {
            var pool = sp.GetRequiredService<GenePool>();
            var router = sp.GetRequiredService<ParetoRouter>();
            var classifier = sp.GetService<L0IntentClassifier>();
            var logger = sp.GetService<ILogger<GeneToRule>>();
            return new GeneToRule(pool, router, classifier, logger);
        });

        services.AddSingleton<SemanticDiffAgent>();

        services.AddSingleton<ArchitectLoop>(sp =>
        {
            var router = sp.GetRequiredService<ParetoRouter>();
            var teacher = sp.GetRequiredService<BootstrapTeacher>();
            var genePool = sp.GetRequiredService<GenePool>();
            var annealer = sp.GetRequiredService<SimulatedAnnealer>();
            var geneToRule = sp.GetRequiredService<GeneToRule>();
            // 延迟解析 ILivingTreeSystem 以打破循环依赖
            var serviceProvider = sp;
            Func<string, CancellationToken, Task<string>> l2Architect = async (query, ct) =>
            {
                var lts = serviceProvider.GetRequiredService<LTAI.AI.Interfaces.ILivingTreeSystem>();
                return await lts.ChatAsync(query, ct).ConfigureAwait(false);
            };
            var logger = sp.GetService<ILogger<ArchitectLoop>>();
            var counterfactual = sp.GetRequiredService<CounterfactualGate>();
            var intentClassifier = sp.GetService<L0IntentClassifier>();
            var semanticAnchor = sp.GetService<SemanticAnchor>();
            var diffAgent = sp.GetRequiredService<SemanticDiffAgent>();
            return new ArchitectLoop(router, teacher, genePool, annealer, geneToRule, l2Architect,
                counterfactualGate: counterfactual, minLoopInterval: TimeSpan.FromMinutes(5),
                intentClassifier: intentClassifier, semanticAnchor: semanticAnchor,
                diffAgent: diffAgent, serviceProvider: sp,
                causalAudit: sp.GetService<RecursiveCausalAudit>(), logger: logger);
        });

        services.AddSingleton<CounterfactualGate>(sp =>
        {
            var logger = sp.GetService<ILogger<CounterfactualGate>>();
            return new CounterfactualGate(0.15, 0.25, embedder: null, logger: logger);
        });

        services.AddSingleton<ParetoRouterSeeder>(sp =>
        {
            var router = sp.GetRequiredService<ParetoRouter>();
            var logger = sp.GetService<ILogger<ParetoRouterSeeder>>();
            var seeder = new ParetoRouterSeeder(router, logger);
            seeder.SeedFromDomainProfiles();
            return seeder;
        });

        // ============================================================================
        // Wire UnifiedSafetyGate delegates into all shell execution paths.
        // This closes the SEC-003 gap: ShellEnv, ShellTools, SkillStepExecutor,
        // and MarkdownToolExecutor all funnel through the same safety gate.
        // Uses delegates (Func<string,string,bool>) to avoid LTAI.Core / LTAI.Tools
        // taking a hard dependency on LTAI.DNA.
        // ============================================================================
        services.AddSingleton(sp =>
        {
            var safetyGate = sp.GetRequiredService<UnifiedSafetyGate>();

            // ShellEnv (L0 — singleton instance)
            global::LTAI.Core.System.ShellEnv.Instance.ExternalSafetyGate =
                (tool, input) =>
                {
                    // Delegate to ShellCommandValidator first (3-layer defense)
                    var (allowed, reason) = global::LTAI.Agent.Tools.ShellCommandValidator.Validate(input);
                    if (!allowed) return false;
                    // Then UnifiedSafetyGate as second layer
                    return safetyGate.EvaluateToolCall(tool, input);
                };

            // ShellTools (L1 — static class)
            global::LTAI.Tools.General.ShellTools.ExternalSafetyGate =
                (tool, input) =>
                {
                    var (allowed, _) = global::LTAI.Agent.Tools.ShellCommandValidator.Validate(input);
                    return allowed ? safetyGate.EvaluateToolCall(tool, input) : false;
                };

            // MAF ShellTools (L1 — Agent.Tools namespace)
            global::LTAI.Agent.Tools.ShellTools.ExternalSafetyGate =
                (tool, input) =>
                {
                    var (allowed, _) = global::LTAI.Agent.Tools.ShellCommandValidator.Validate(input);
                    return allowed ? safetyGate.EvaluateToolCall(tool, input) : false;
                };

            return new SafetyGateWireUp(); // marker singleton to ensure wiring runs once
        });

        // Wire MemoryEventBus subscribers
        services.AddSingleton<EventBusWireUp>(sp =>
        {
            var eventBus = sp.GetRequiredService<LTAI.Core.System.IMemoryEventBus>();
            var auditLog = sp.GetService<LTAI.Core.System.AuditLogService>();

            // AuditLogService: record all memory events
            if (auditLog != null)
            {
                eventBus.SubscribeAll(evt =>
                {
                    auditLog.Record(
                        evt.Source,
                        evt.Type.ToString(),
                        evt.Detail,
                        riskScore: evt.Metadata.TryGetValue("importance", out var imp)
                            ? Convert.ToDouble(imp) : null,
                        result: "recorded"
                    );
                });
            }

            return new EventBusWireUp();
        });

        return services;
    }

    public static IServiceCollection AddLTAIAgentsFromYaml(this IServiceCollection services, string yamlPath)
    {
        var yaml = File.ReadAllText(yamlPath);
        var config = ParseYamlConfig(yaml);
        services.AddSingleton(config);

        foreach (var card in config.Agents)
        {
            services.AddKeyedScoped(card.Name, (sp, _) => CreateAgent(sp, card));
        }

        return services;
    }

    private static AIAgent CreateAgent(IServiceProvider sp, LTAIAgentCard card)
    {
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var chatClient = sp.GetRequiredService<IChatClient>();
        var skillRegistry = sp.GetRequiredService<Agents.SkillRegistry>();

        AIAgent agent = card.Type switch
        {
            AgentType.Chat => new ChatAgent(card, chatClient, skillRegistry,
                loggerFactory.CreateLogger<ChatAgent>()),
            AgentType.Code => new CodeAgent(card, chatClient, skillRegistry,
                loggerFactory.CreateLogger<CodeAgent>()),
            AgentType.EIA => new EIAAgent(card, chatClient, skillRegistry,
                loggerFactory.CreateLogger<EIAAgent>()),
            AgentType.Reasoning => new ReasoningAgent(card, chatClient, skillRegistry,
                loggerFactory.CreateLogger<ReasoningAgent>()),
            _ => new ChatAgent(card, chatClient, skillRegistry,
                loggerFactory.CreateLogger<ChatAgent>())
        };

        return ApplyMiddleware(agent, card, sp);
    }

    private static AIAgent ApplyMiddleware(AIAgent agent, LTAIAgentCard card, IServiceProvider sp)
    {
        var builder = agent.AsBuilder();

        foreach (var mwName in card.Middleware)
        {
            switch (mwName)
            {
                case "unified_safety":
                    var safetyGate = sp.GetRequiredService<UnifiedSafetyGate>();
                    builder.Use(async (messages, session, options, inner, ct) =>
                    {
                        var msgList = messages.ToList();
                        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);
                        var sessionId = (session as LTAIAgentSession)?.SessionId ?? "anon";

                        var gateVerdict = await safetyGate.EvaluateInputAsync(
                            userMsg?.Text ?? "", sessionId, ct);

                        if (!gateVerdict.IsAllowed)
                            return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                                $"[Safety] {gateVerdict.Reason}"));

                        if (gateVerdict.Action == GateAction.Warn)
                        {
                            var logger = sp.GetRequiredService<ILoggerFactory>()
                                .CreateLogger("UnifiedSafety");
                            logger.LogWarning("Safety warning for {Agent}: {Reason}", card.Name, gateVerdict.Reason);
                        }

                        var response = await inner.RunAsync(messages, session, options, ct).ConfigureAwait(false);

                        if (response.Text is not null)
                        {
                            var outputVerdict = await safetyGate.EvaluateOutputAsync(
                                response.Text, sessionId, ct).ConfigureAwait(false);
                            if (!outputVerdict.IsAllowed)
                                return new AgentResponse(new ChatMessage(ChatRole.Assistant,
                                    $"[Safety] Output filtered: {outputVerdict.Reason}"));
                        }

                        return response;
                    }, null);
                    break;
                case "budget_tracking":
                    var budgetTracking = sp.GetRequiredService<BudgetTrackingMiddleware>();
                    builder.Use(budgetTracking.InvokeAsync, null);
                    break;
            }
        }

        return builder.Build();
    }

    private static LTAI.Models.AgentConfig ParseYamlConfig(string yaml)
    {
        var config = new LTAI.Models.AgentConfig();
        var lines = yaml.Split('\n');
        LTAIAgentCard? currentAgent = null;
        string currentSection = "";
        var currentTools = new List<string>();
        var currentMiddleware = new List<string>();
        var currentOptions = new Dictionary<string, object?>();
        var instructionsBuilder = new System.Text.StringBuilder();
        bool inInstructions = false;
        bool inTools = false;
        bool inMiddleware = false;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            if (trimmed.StartsWith("global:")) { currentSection = "global"; continue; }
            if (trimmed.StartsWith("agents:")) { currentSection = "agents"; continue; }

            if (trimmed.StartsWith("- name:") && currentSection == "agents")
            {
                FinalizeCurrentAgent();
                currentAgent = new LTAIAgentCard { Name = trimmed[8..].Trim() };
                currentTools.Clear();
                currentMiddleware.Clear();
                currentOptions.Clear();
                inInstructions = false;
                inTools = false;
                inMiddleware = false;
                instructionsBuilder.Clear();
                continue;
            }

            if (currentAgent is null) continue;
            var t = trimmed.TrimStart();

            if (t.StartsWith("type:"))
            {
                inInstructions = inTools = inMiddleware = false;
                currentAgent.Type = t[5..].Trim() switch
                {
                    "code_agent" => AgentType.Code,
                    "eia_agent" => AgentType.EIA,
                    "reasoning_agent" => AgentType.Reasoning,
                    _ => AgentType.Chat
                };
            }
            else if (t.StartsWith("model:"))
            {
                inInstructions = inTools = inMiddleware = false;
                currentAgent.Model = t[6..].Trim();
            }
            else if (t.StartsWith("instructions:") && t.Length > 15 && t[14] == '|')
            {
                inInstructions = true; inTools = false; inMiddleware = false;
            }
            else if (t.StartsWith("middleware:"))
            {
                inInstructions = false; inTools = false; inMiddleware = true;
            }
            else if (t.StartsWith("tools:"))
            {
                inInstructions = false; inTools = true; inMiddleware = false;
            }
            else if (t.StartsWith("options:"))
            {
                inInstructions = inTools = inMiddleware = false;
            }
            else if (inInstructions && (t.StartsWith("- ") || !t.Contains(":")))
            {
                var content = t.StartsWith("- ") ? t[2..] : t;
                instructionsBuilder.AppendLine(content);
            }
            else if (inTools && t.StartsWith("- "))
            {
                currentTools.Add(t[2..].Trim());
            }
            else if (inMiddleware && t.StartsWith("- "))
            {
                currentMiddleware.Add(t[2..].Trim());
            }
            else if (!inInstructions && !inTools && !inMiddleware && t.Contains(":"))
            {
                var colonIdx = t.IndexOf(':');
                var key = t[..colonIdx].Trim();
                var val = t[(colonIdx + 1)..].Trim();
                if (double.TryParse(val, out var dv)) currentOptions[key] = dv;
                else if (int.TryParse(val, out var iv)) currentOptions[key] = iv;
                else currentOptions[key] = val;
            }
        }

        FinalizeCurrentAgent();

        void FinalizeCurrentAgent()
        {
            if (currentAgent is null) return;
            currentAgent.Instructions = instructionsBuilder.ToString().Trim();
            currentAgent.Tools = new List<string>(currentTools);
            currentAgent.Middleware = new List<string>(currentMiddleware);
            currentAgent.Options = new Dictionary<string, object?>(currentOptions);
            config.Agents.Add(currentAgent);
        }

        return config;
    }

    public static void WirePlanningPipeline(IServiceProvider sp)
    {
        var plannerCritic = sp.GetRequiredService<PlannerCriticWorkflow>();
        var factory = sp.GetRequiredService<AgentFactory>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("LTAI.Agent");

        // Register default planner-critic-executor triads per domain
        foreach (var domain in new[] { "code", "eia", "general" })
        {
            try
            {
                var agent = factory.GetOrCreate(domain);
                var planner = factory.GetOrCreate($"{domain}_planner");
                var critic = factory.GetOrCreate($"{domain}_critic");

                plannerCritic.RegisterAgent(domain, agent);
                plannerCritic.RegisterAgent($"{domain}_planner", planner);
                plannerCritic.RegisterAgent($"{domain}_critic", critic);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to register planner-critic pair for domain '{Domain}'", domain);
            }
        }
    }
}

/// <summary>
/// Marker type for DI — ensures the UnifiedSafetyGate delegate wiring runs exactly once at startup.
/// </summary>
file sealed record SafetyGateWireUp;
file sealed record EventBusWireUp;
