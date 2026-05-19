# LTAI .NET Migration Plan (详细)

> 源仓库: https://github.com/ookok/LivingTreeAlAgent (Python ~700模块)  
> 目标仓库: https://github.com/ookok/ltai4net (.NET 10)  
> 更新: 2026-05-19 | 整体覆盖率 ~38%

## Status

| Phase | Status | Description |
|-------|:------:|-------------|
| Core (LTAI.Core) | ✅ Done | ICognitiveMesh, IToolRegistry, ILayerGovernor, IProviderEngine, Handshake/Journal models, LTAIOptions, ContextFolding, EIAModels, ChatClientExtensions |
| AI (LTAI.AI) | ✅ Done | 11 Governors + ProviderEngine (IChatClient wrapper) + LivingTreeSystem + SystemGuardian |
| Web (LTAI.Web) | ✅ Done | POST /api/chat, GET /api/status, GET /api/health; RateLimiting → ASP.NET Core built-in |
| Vector (LTAI.Vector) | ✅ Done | IVectorStore, VectorStore, Embedding, DocumentStore (SQLite+FTS5), KnowledgeBase, KG, Reranker, AgenticRAG, StructMemory, DeepKnowledge, KernelMemoryStore, IEmbeddingGenerator |
| Browser (LTAI.Browser) | ✅ Done | PuppeteerSharp, AdaptiveExtractor |
| Document (LTAI.Document) | ✅ Done | UniversalFileParser, 9 parsers |
| Network (LTAI.Network) | ✅ Done | P2PNode, ServiceDiscovery, SmartDnsResolver, MassTransitMessageBus |
| TreeLLM (LTAI.TreeLLM) | ✅ Done | 6路由策略 + HolisticElection + ElectionBus + AutoPrompt; CircuitBreaker → Polly; SemanticCache, CoherenceGate, StrategicDistiller, DeepRouting, ModelRegistry, ForesightGate |
| Execution (LTAI.Execution) | ✅ Done | TaskTree, ReactExecutor (47 tools), DAGExecutor, BatchExecutor, Orchestrator, QualityChecker, SelfHealer |
| Memory (LTAI.Memory) | ✅ Done | UserModel (L1-L3), PersonaMemory, EmotionalMemory (Plutchik), MemoryPolicy (MemPO), TraitEvolution, MemoryOrchestrator |
| Economy (LTAI.Economy) | ✅ Done | Metabolism, EconomicEngine, ThermoBudget, GRPO, InverseReward |
| DNA (LTAI.DNA) | ✅ Done | DNADeep, DualConsciousness, EvolutionDriver, LifeEngine, SafetyCoordinator, DNAOrchestrator, DNAEndpoints, DNAModels |
| Capability (LTAI.Capability) | ✅ Done | MathReasoner, FormalLogicEngine, DialecticalReasoner, ReasoningOrchestrator, MultiLangCodeAnalyzer, UnifiedSearchEngine, DocumentProcessor, MapServices(4地图), GatewayServices, CodeReviewEngine |
| Host (LTAI.Host) | ✅ Done | Program.cs; OpenTelemetry + Serilog |
| MAF (LTAI.MAF) | ✅ Done | LTAIAgent, A2AHost, MAFEndpoints, GovernorMiddleware |
| MCP (LTAI.MCP) | ✅ Done | MCPServer, Protocol, Transports, tools/resources |
| Metrics (LTAI.Metrics) | ✅ Done | LTAIMetricsCollector, MetricsExtensions |
| Multimodal | ✅ Done | MultimodalEndpoints, MultimodalServices |
| Sandbox | ✅ Done | DockerSandbox, ProcessSandbox, SandboxOrchestrator |
| TUI (LTAI.TUI) | 🟡 In Progress | StreamRenderer, DiffEngine, TaskDagView, TuiInputBox, etc. |
| Desktop (LTAI.Desktop) | 🟡 In Progress | MAUI: Dashboard/Chat/Files/Settings |
| WebApp (LTAI.WebApp) | 🟡 In Progress | Blazor: Chat/Code/Config/Dashboard/Files/Git/Knowledge |
| Benchmarks | ✅ Done | BenchmarkDotNet + PublishAot |
| Observability | ✅ Done | OpenTelemetry + Serilog |

## Phase 1-4: 基础设施标准化 — 已完成

| 任务 | 自研 → 替换 | 状态 |
|------|-----------|:----:|
| LLM 抽象 | IProviderEngine → Microsoft.Extensions.AI.IChatClient | ✅ |
| 限流器 | RateLimitingMiddleware → ASP.NET Core Rate Limiter | ✅ |
| 断路器 | CircuitBreaker → Polly (Microsoft.Extensions.Resilience) | ✅ |
| 嵌入抽象 | IEmbeddingBackend → IEmbeddingGenerator | ✅ |
| 可观测性 | — → OpenTelemetry + Serilog | ✅ |

## Phase 2: RAG/知识层替换 — 已完成

| 任务 | 自研 → 替换 | 状态 |
|------|-----------|:----:|
| 文档存储 | DocumentStore (SQLite+FTS5) → Kernel Memory | ✅ |
| 向量存储 | ConcurrentDictionary → Kernel Memory + IEmbeddingGenerator | ✅ |
| 知识检索 | KnowledgeBase → Kernel Memory Search API | ✅ |

## Phase 3: 多智能体编排 — 已完成

| 任务 | 自研 → 替换 | 状态 |
|------|-----------|:----:|
| 治理层 | 11 Governors → MAF Middleware + Workflow | ✅ |
| 编排 | Orchestrator → MAF SequentialWorkflow | ✅ |
| 工具 | CapabilityGovernor → MAF Tool/Function middleware | ✅ |

## Phase 4: 网络/生产化 — 已完成

| 任务 | 自研 → 替换 | 状态 |
|------|-----------|:----:|
| P2P通信 | P2PNode → MAF A2A | ✅ |
| 消息总线 | Channel\<T\> → MassTransit + RabbitMQ | ✅ |
| 部署 | Program.cs → MAF Hosting | ✅ |
| 可观测性 | — → OpenTelemetry (完整) | ✅ |

## 老项目缺口对照

详见 `ECOSYSTEM_ANALYSIS.md` 第 12 节 + `COVERAGE_ANALYSIS.md` 模块映射。

### 已完成移植 (覆盖率 ≥70%)

| 子系统 | .py 文件 | 覆盖率 | 状态 |
|--------|:------:|:---:|:----:|
| `memory/` | 7 | 100% | ✅ 比Python更丰富 |
| `mcp/` | 2 | ≥100% | ✅ 协议更完整 |
| `reasoning/` | 10 | 90% | ✅ 4/4推理完成 |
| `optimization/` | 3 | 80% | ✅ GRPO覆盖LPO |
| `knowledge/` | 50 | 70% | ✅ RAG核心完成 |
| `tui/` | ~15 | 60% | 🟡 Spectre.Console |

### 待深度移植 (覆盖率 25%~45%)

| 子系统 | .py 文件 | 覆盖率 | 缺口 |
|--------|:------:|:---:|------|
| `treellm/` | 118 | 45% | 73个模块: 多臂路由/预测路由/健康预测/会话绑定/对抗门/自改进/连续基准 |
| `dna/` | 123 | 40% | 115个模块: 世界模型/心智旅行/哥德尔/多人格/自进化深度/现象意识/激素/免疫/熵驱动 |
| `execution/` | 40 | 40% | 27个模块: 扩散计划器/GTSM/检查点/成本感知 |
| `capability/` | 95 | 35% | 64个模块: 技能系统/工具市场/管道引擎/代码图谱/爬虫 |
| `observability/` | 16 | 35% | 14个模块: 评估框架/审计/统计验证 |
| `api/` | 27 | 30% | 19个模块: HTMX/WebSocket/OAuth |
| `network/` | 33 | 25% | 23个模块: 分布式意识/WebRTC/NAT穿透/信誉 |
| `core/` | 74 | 25% | 55个模块: 硬件加速/TTS/数字孪生/行为树 |

### 未开始移植

| 子系统 | .py 文件 | 说明 |
|--------|:------:|------|
| `templates/` | 12 | Jinja2 HTML/HTMX 模板 |
| `cell/` | ~10 | 训练/梦境/蒸馏 |
| `market/` | ~5 | Agent 市场 |
| `client/` | 467 | Electron 前端 (MAUI/Blazor 仅基本覆盖) |

## Phase 5b: DNA 深层进化 (下一步)

| 组件 | 功能 | 对标 Python |
|------|------|-------------|
| WorldModel | 世界建模与预测 | `world_model.py` `predictive_world.py` |
| MentalTimeTravel | 心智旅行/前瞻规划 | `mental_time_travel.py` |
| GodelianSelf | 哥德尔自我引用的自省能力 | `godelian_self.py` |
| SelfEvolutionDeep | 深层自进化 + 蜂群进化 | `self_evolution.py` `swarm_evolution.py` |
| SheshaHeads | 8角色多人格系统 | `shesha_heads.py` |
| PhenomenalConsciousness | 现象意识 | `phenomenal_consciousness.py` |
| HormoneSignaling | 激素信号与生物节律 | `hormone_signaling.py` `biorhythm.py` |
| ImmuneSystem | 先天+适应性免疫系统 | `immune_system.py` |
| EntropyDrive | 新奇/熵驱动与惊奇门控 | `entropy_drive.py` `surprise_gating.py` |
| PlayEngine | 8种游戏的游玩学习 | `play_engine.py` |
| MetaCognition | 元记忆/元优化/元策略 | `meta_memory.py` `meta_optimizer.py` `meta_strategy.py` |
| IdentitySystem | 身份与人格形成 | `identity.py` `personality.py` `self_narrative.py` |
| MultistreamReasoning | 多流推理 | `multistream.py` |
| ToolRepair | 工具自修复 | `tool_repair.py` |
| RLVRMonitor | RLVR坍缩检测 | `rlvr_monitor.py` |
| DiffusionBridge | 扩散桥 | `diffusion_bridge.py` |
| LivingCompiler | 活性编译器 | `living_compiler.py` |

## Phase 6b: TreLLM 深层路由 (下一步)

| 组件 | 功能 | 对标 Python |
|------|------|-------------|
| BanditRouter | 多臂赌博机路由 | `bandit_router.py` |
| PredictiveRouter | 预测性路由 | `predictive_router.py` |
| BudgetRouter | 预算感知路由 | `budget_router.py` |
| HealthPredictor | 提供者健康预测 | `health_predictor.py` |
| LatencyOracle | 延迟预言 | `latency_oracle.py` |
| SessionBinding | 会话绑定 | `session_binding.py` `session_compressor.py` |
| CrossSessionBridge | 跨会话知识传递 | `cross_session_bridge.py` |
| AdversarialGate | 对抗门 | `adversarial_gate.py` |
| AdversarialSelfplay | 对抗自弈 | `adversarial_selfplay.py` |
| SelfImprover | 自改进循环 | `self_improver.py` |
| ContinuousBenchmark | 连续基准测试 | `continuous_benchmark.py` |
| ConnectionPool | 连接池 | `connection_pool.py` |
| CompetitiveEliminator | 提供者淘汰 | `competitive_eliminator.py` |
| ScoreMatchingRouter | 分数匹配路由 | `score_matching_router.py` |
| ConcurrentStream | 并发流处理 | `concurrent_stream.py` |
| ThreeModelIntelligence | 三模型智能 | `three_model_intelligence.py` |
| FluidCollective | 流体集体智能 | `fluid_collective.py` |
| SegmentedKVCompressor | KV缓存压缩 | `segmented_kv_compressor.py` |

## .NET AI Ecosystem Reference

### Microsoft Stack (全 MIT 许可)
- **Microsoft.Extensions.AI** — Core abstractions (IChatClient, IEmbeddingGenerator), model-agnostic
- **Microsoft Agent Framework** — Multi-agent orchestration (sequential, group, handoff)
- **Semantic Kernel** — Official LLM orchestration SDK (plugins, memory, planners)
- **Kernel Memory** — RAG pipeline with automatic ingestion
- **Microsoft.Extensions.VectorData** — Vector store abstraction layer
- **Microsoft.Extensions.Resilience** — Polly-based resilience (retry, circuit breaker, timeout)
- **Microsoft.ML.Tokenizers** — High-performance tokenization
- **ML.NET** — Traditional ML / AutoML

### Open-Source Projects (全免费)
- **Polly** — Resilience (retry, circuit breaker, timeout)
- **LanceDB** — Embedded vector database (Rust engine)
- **MassTransit** — Enterprise message bus
- **Quartz.NET** — Job scheduling
- **Serilog** — Structured logging
- **OpenTelemetry** — Distributed tracing/metrics/logging
- **NPOI / PdfPig** — Office document processing
- **Roslyn** — Code analysis
- **Spectre.Console** — Terminal UI

## Build Status
- 16 项目 (在 .sln 中): 0 errors, 9 warnings
- 7 项目 (未入 .sln): 待集成
- 5 测试项目 (未入 .sln): 待集成
- Target: .NET 10
