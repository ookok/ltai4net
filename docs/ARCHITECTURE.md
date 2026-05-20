# LTAI4Net 架构文档

> 更新时间: 2026-05-19 | 429 个 .cs 文件 | 26 项目 | 0 错误

## 总体架构

```
┌──────────────────────────────────────────────────────────────┐
│                    入口层 (Entry Points)                      │
│  LTAI.Host (ASP.NET) │ LTAI.TUI (Terminal) │ LTAI.Desktop (MAUI) │
│  LTAI.WebApp (Blazor) │ LTAI.MCP (Console)                    │
└──────────────────────────────────────────────────────────────┘
                              │
┌──────────────────────────────────────────────────────────────┐
│                    Web API 层 (LTAI.Web)                      │
│  16 端点: Chat │ Auth │ Audit │ SSE │ Proxy │ Code │ Doc │  │
│  Workspace │ Cognition │ Github │ WeWork │ OpenCode          │
└──────────────────────────────────────────────────────────────┘
                              │
┌──────────────────────────────────────────────────────────────┐
│                  AI 治理层 (LTAI.AI)                          │
│  13 Governors: Capability │ Routing │ Context │ Self │ Task  │
│  Input │ Output │ Storage │ Evolution │ System Guardian      │
│  + CapabilityBus 统一能力总线                                  │
└──────────────────────────────────────────────────────────────┘
                              │
┌──────────┬──────────┬──────────┬──────────┬──────────────────┐
│ TreeLLM  │   DNA    │Execution │ Capability│   Network/P2P    │
│ ──────── │ ──────── │───────── │────────── │ ──────────────── │
│ 路由选举  │ 意识涌现  │ 任务规划  │ 代码分析   │ P2P 节点/发现    │
│ 提示工程  │ 身份人格  │ 质量验证  │ 文档解析   │ 加密通道/NAT    │
│ L1/L2协作 │ 进化驱动  │ 自愈修复  │ GIS/推理   │ 蜂群/分布式意识  │
│ 会话管理  │ 安全免疫  │ 会话压缩  │ 技能/工具   │ MassTransit总线 │
│ 对抗自弈  │ 元认知    │ 检查点   │ 爬虫/搜索   │ 离线/信誉       │
│ ContextMoE│ 生命引擎  │ 思考进化  │ 知识觅食   │ 空间感知/生物识 │
└──────────┴──────────┴──────────┴──────────┴──────────────────┘
                              │
┌──────────────────────────────────────────────────────────────┐
│                    基础设施层 (LTAI.Core)                      │
│  配置加密 │ 事件总线 │ 资源树VFS │ 序列化 │ 多媒体 │ 韧性    │
│  上下文预算 │ 记忆分层 │ 会话恢复 │ 渐进信任 │ IO优化  │       │
└──────────────────────────────────────────────────────────────┘
                              │
┌──────────┬──────────┬──────────┬──────────┬──────────────────┐
│  Vector  │  Memory  │ Economy  │ Metrics  │  Market / Cell   │
│ ──────── │ ──────── │───────── │────────── │ ──────────────── │
│ RAG检索   │ 情感记忆  │ 代谢预算  │ 评估框架  │ 市场情报/收入    │
│ 知识图谱  │ 人格记忆  │ GRPO优化 │ 审计日志  │ Cell训练/分裂    │
│ 向量存储  │ 用户模型  │ 逆奖励   │ 动态策略  │                  │
│ KMem集成  │ MemPO策略│          │ 活动流   │                  │
└──────────┴──────────┴──────────┴──────────┴──────────────────┘
                              │
┌──────────────────────────────────────────────────────────────┐
│                  外部集成层                                    │
│  MAF (A2A协议) │ Sandbox (Docker/Process) │ MCP Server       │
│  Multimodal │ Document/Parsers │ Browser (Playwright)        │
│  Templates (Razor)                                              │
└──────────────────────────────────────────────────────────────┘
```

## 各项目详细架构

### LTAI.Core (56 文件)
基础库：系统服务、配置、消息、模型、韧性、序列化、多媒体、加速

| 模块 | 文件数 | 核心类 |
|------|:---:|------|
| System | 18 | ResourceTree(VFS), AgentQA, ContextBudget, CollectiveIntel, TaskStateManager, PromptInjector, ProgressiveTrust, DecoupledExecutor, SeedDevice, FileResolver, SessionResilience, VitalsMonitor, UnifiedRegistry, PromptShield, ShellEnv, UniversalScanner, ConcurrencyGuard, DpoPrefs, AtomicModification, AsyncDisk |
| Configuration | 4 | LTAIOptions, ProviderRegistry(29 providers), SecretVault(AES-256-GCM), ConfigSecurity |
| Acceleration | 3 | HardwareAcceleration, MemoryOptimizer, IOOptimizer |
| Serialization | 3 | ProtoSerializer, JsonUtils, ServiceStubs |
| Multimodal | 3 | WhisperSttEngine, TtsEngine, FfmpegMediaProcessor |
| Life | 4 | AutonomousGrowth, BehaviorTree, DigitalTwin, SynapticPlasticity |
| Resilience | 2 | ResilienceBrain, SystemHealth(GreenScheduler) |
| Messaging | 3 | CognitiveMesh, EventBusV2(12器官), ToolRegistry |
| Models | 7 | EntityRegistry, ContextFolding, EIAModels, Enums, Handshake, JournalEntry, LayerStats |
| Interfaces | 5 | ICognitiveMesh, ILayerGovernor, IProviderEngine, IToolRegistry, ChatClientExtensions |
| Execution | 1 | TaskJournal |

### LTAI.AI (17 文件)
AI 治理层："活树" 13 Governor 层次结构 + 统一能力总线

| 模块 | 核心类 |
|------|------|
| Governors | **LivingTreeSystem**, CapabilityGovernor, RoutingGovernor, ContextGovernor, SelfGovernor, TaskGovernor, InputGovernor, OutputGovernor, StorageGovernor, EvolutionGovernor, CommunicationGovernor, SystemGuardian, LayerGovernor(基类), **CapabilityBus**(10 适配器类型统一 invoke) |
| Providers | ProviderEngine(IChatClient 封装) |
| Utilities | GovernorUtilities |

### LTAI.Web (16 文件)
ASP.NET Core Minimal API 端点层

| 端点文件 | 功能 |
|------|------|
| LTAIApiEndpoints | POST /api/chat 主入口 |
| LTAIAuthEndpoints | 认证 |
| GithubAuthEndpoints | GitHub OAuth |
| AuditEndpoints | 审计日志查询 |
| CodeApiEndpoints | 代码执行 API |
| DocRoutesEndpoints | 文档处理 |
| SseAgentEndpoints | SSE 实时推送 |
| OpenAIProxyEndpoints | OpenAI 兼容代理 |
| OpenCodeBridgeEndpoints | 代码桥接 (8 环境变量 Provider) |
| WeWorkBotEndpoints | 企业微信 Bot |
| CognitionStreamEndpoints | 认知流 SSE 端点 |
| WorkspaceEndpoints | 多用户工作空间 CRUD |
| SessionCache | 会话缓存 (LRU + TTL) |
| RequestBuffer | 请求缓冲 + 背压 |
| TokenAccountant | 四层 Token 边际分配 |

### LTAI.TreeLLM (39 文件)
LLM 编排核心：路由、提示工程、会话、对抗、智能

| 模块 | 核心类 |
|------|------|
| Routing (5) | Router(6策略), HolisticElection(16 provider 能力), ModelRegistry, ElectionBus, DeepRouting |
| Prompting (6) | AutoPrompt(Thompson), PromptVersioning, PromptCoach, OntoPromptBuilder, PromptOptimizer(6角色), PromptEngine(DSPy) |
| Session (10) | ContextMoE(5层专家), ContinuousConsciousness, SessionBinding, SessionCompressor, CrossSessionBridge, ConnectionPool, FreeModelPool(10 providers), SegmentedKVCompressor, DataValueDensity, SelfImprover |
| Intelligence (4) | ThreeModelIntelligence(脊髓反射/分流/情绪), L1L2Collaboration(need标签), FluidCollective(stigmergic), Acceleration(预热) |
| Adversarial (4) | AdversarialGate, AdversarialSelfPlay, ConcurrentStream, TokenCircuitBreaker |
| Health/Resilience (5) | HealthPredictor, ReasoningBudget, CircuitBreaker, DebugLoop, ErrorInterceptor |
| Strategic (2) | ForesightGate, StrategicDistiller |
| Caching (1) | SemanticCache |
| Models (1) | TreeLLMModels |

### LTAI.DNA (30 文件)
意识/进化/生命/安全层

| 模块 | 核心类 |
|------|------|
| Consciousness (5) | ConsciousnessEmergence, DualConsciousness, IdentitySystem, Personality, PhenomenalConsciousness |
| Life (9) | BiorhythmClock, ContextEngineer, HormoneSignaling, LifeEngine, LivingCompiler, LivingPresence, LocalIntelligence, PlayEngine, SheshaHeads(8头) |
| Evolution (3) | EvolutionDriver, MultiStreamEngine, SurpriseGatedMemory |
| Meta (3) | MetaMemory, MetaOptimizer, MetaStrategy |
| Safety (5) | DiffusionBridge, ImmuneSystem, RLVRMonitor, SafetyCoordinator, ToolRepair |
| Models (1) | DNAModels |
| Root (4) | DNADeep, DNAEndpoints, DNAOrchestrator, ServiceCollectionExtensions |

### LTAI.Execution (26 文件)
任务执行与质量保证

| 模块 | 核心类 |
|------|------|
| Planning (8) | DiffusionPlanner, GtsmPlanner, CheckpointManager, ThinkingEvolution, CostAware, CoffeeEngine, RecursiveDecomposer, TaskTree |
| Quality (5) | AutoSkillResolver, Clarifier, FitnessLandscape, RankMonitor, ThompsonDelegator |
| Modes (3) | ReactExecutor(47工具), DAGExecutor, BatchExecutor |
| Session (4) | GlobalRulePool, SessionManager, SideGit, TerminalCompressor |
| Root (5) | Orchestrator, QualityChecker, QualityScorer, SelfHealer, ExecutionEndpoints |

### LTAI.Network (23 文件)
P2P 网络与分布式系统

| 模块 | 核心类 |
|------|------|
| Links (5) | EncryptedChannel, MessageBusBinary, NATTraverser, OfflineMode, Reputation |
| Consensus (3) | DistributedConsciousness, SwarmCoordinator, Collective |
| Perception (3) | BiometricSignature, P2PPresence, SpatialAwareness |
| Acceleration (2) | ExternalAccess, NetworkResilience |
| Bridge (2) | ChannelBridge, ReachGateway |
| Root (6) | P2PNode, IP2PNode, ServiceDiscovery, SmartDnsResolver, MassTransitMessageBus, NetworkEndpoints |

### LTAI.Capability (40 文件)
能力生态：代码、文档、GIS、集成、推理、搜索、技能、工具

| 模块 | 核心类 |
|------|------|
| Tools (7) | ToolMarket, ToolMeta, ToolOrchestrator, ToolSynthesis, VfsAdapter, PublicApisResource, ProcessingFramework |
| Integration (7) | GatewayServices, MessageGateway, WeWorkBot, WeWorkCrypt, PkgManager, SelfUpdater, SmsGateway |
| Evolution (3) | SelfDiscovery, SelfDocumentation, SelfModifier |
| DocEngine (3) | DocEngine, DocForge, DocPipeline |
| Reasoning (4) | MathReasoner, FormalLogicEngine, DialecticalReasoner, ReasoningOrchestrator |
| Skills (3) | SkillBuckets, SkillDiscovery, SkillFactory |
| Other (9) | MultiLangCodeAnalyzer, CodeGraph, LightCrawler, DocumentProcessor, GIS, KnowledgeForager, PipelineEngine, UnifiedSearchEngine, CodeReviewEngine |

### 其他项目

| 项目 | 文件数 | 功能 |
|------|:---:|------|
| LTAI.Vector | 20 | 向量存储 + Kernel Memory + AgenticRAG + 知识图谱 + Reranker |
| LTAI.Memory | 8 | 情感记忆(Plutchik) + 人格记忆 + 用户模型(L1-L3) + MemPO |
| LTAI.Metrics | 12 | AgentEval + EvalHarness + StatisticalValidator + 审计 + 监控 |
| LTAI.Economy | 6 | 代谢预算 + ThermoBudget + GRPO + InverseReward |
| LTAI.Host | 1 | Program.cs 入口 + OpenTelemetry + Serilog |
| LTAI.Document | 6 | UniversalFileParser + 9 解析器 (NPOI/PdfPig/Markdig) |
| LTAI.Browser | 5 | PlaywrightBrowserAgent + AdaptiveExtractor |
| LTAI.Cell | 8 | Cell训练 + 分裂 + 蒸馏 + 梦学习 + 再生 |
| LTAI.Market | 7 | 市场情报 + 机会评分 + 用户画像 + 收入引擎 |
| LTAI.MAF | 5 | 多智能体框架 A2A 协议 |
| LTAI.MCP | 4 | MCP Server JSON-RPC 2.0 |
| LTAI.Sandbox | 6 | Docker/Process 隔离执行 |
| LTAI.Multimodal | 3 | 多模态端点 + 服务 |
| LTAI.TUI | 13 | Spectre.Console 终端界面 |
| LTAI.Desktop | 7 | MAUI 桌面应用 (4页面) |
| LTAI.WebApp | 1 | Blazor Server WebApp (7页面) |
| LTAI.Templates | 1 | Razor 模板 |

## 技术栈

| 层 | 技术 | 依据 |
|------|------|------|
| 运行时 | .NET 10 | 最高性能 AOT |
| LLM 网关 | Microsoft.Extensions.AI (IChatClient) | 29 厂商兼容 |
| Agent 框架 | 自研 CognitiveMesh + 13 Governor + MAF A2A | 仿生 + 微软标准 |
| Web | ASP.NET Core Minimal API | 微软生态 |
| 韧性 | Polly (Microsoft.Extensions.Resilience) | .NET 标准 |
| 消息总线 | MassTransit + RabbitMQ | 企业级 |
| 可观测性 | OpenTelemetry + Serilog | 全链路 |
| RAG | Kernel Memory + 自研 | 文档/语义搜索 |
| 文档 | NPOI + PdfPig + Markdig | 多格式 |
| DB | Microsoft.Data.Sqlite + FTS5 | 全文搜索 |
| 代码分析 | Roslyn + MultiLangCodeAnalyzer (13语言) | 多语言 |
| P2P | Channel + MassTransit + SmartDNS | 异步消息 |
| 序列化 | System.Text.Json + Google.Protobuf | AOT 友好 |
| 加密 | AES-256-GCM (SecretVault) | 机器指纹密钥 |
| 测试 | xUnit + BenchmarkDotNet | 性能基准 |
