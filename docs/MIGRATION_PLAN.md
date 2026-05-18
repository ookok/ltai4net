# LTAI .NET Migration Plan

> 源仓库: https://github.com/ookok/LivingTreeAlAgent (Python ~700模块)  
> 目标仓库: https://github.com/ookok/ltai4net (.NET 10)  
> 更新: 2026-05-18 | Phase 1 进行中

## Status

| Phase | Status | Description |
|-------|:------:|-------------|
| Core (LTAI.Core) | ✅ Done | ICognitiveMesh, IToolRegistry, ILayerGovernor, IProviderEngine, Handshake/Journal models, LTAIOptions, ContextFolding, EIAModels |
| AI (LTAI.AI) | ✅ Done | 10 Governors + ProviderEngine (IChatClient wrapper) + LivingTreeSystem + SystemGuardian |
| Web (LTAI.Web) | 🔄 Migrating | POST /api/chat, GET /api/status, GET /api/health; RateLimiting → ASP.NET Core built-in |
| Vector (LTAI.Vector) | ✅ Done | IVectorStore, VectorStore, Embedding, DocumentStore (SQLite+FTS5), KnowledgeBase, KG, Reranker, AgenticRAG, StructMemory |
| Browser (LTAI.Browser) | ✅ Done | PuppeteerSharp, AdaptiveExtractor |
| Document (LTAI.Document) | ✅ Done | UniversalFileParser, 9 parsers |
| Network (LTAI.Network) | ✅ Done | P2PNode, ServiceDiscovery |
| TreeLLM (LTAI.TreeLLM) | 🔄 Migrating | 6路由策略 + HolisticElection + ElectionBus + AutoPrompt; CircuitBreaker → Polly |
| Execution (LTAI.Execution) | ✅ Done | TaskTree, ReactExecutor (47 tools), DAGExecutor, BatchExecutor, Orchestrator, QualityChecker, SelfHealer |
| Memory (LTAI.Memory) | ✅ Done | UserModel (L1-L3), PersonaMemory, EmotionalMemory (Plutchik), MemoryPolicy (MemPO), TraitEvolution |
| Economy (LTAI.Economy) | ✅ Done | Metabolism, EconomicEngine, ThermoBudget, GRPO, InverseReward |
| Host (LTAI.Host) | 🔄 Migrating | Program.cs; adding OpenTelemetry + Serilog |
| Observability | 🟡 Starting | OpenTelemetry + Serilog (Phase 1) |

## Phase 1 (Current): 基础设施标准化 — 用微软生态替换自研组件

| 任务 | 自研 → 替换 | 状态 |
|------|-----------|:----:|
| LLM 抽象 | IProviderEngine → Microsoft.Extensions.AI.IChatClient | 🔄 已部分完成 |
| 限流器 | RateLimitingMiddleware → ASP.NET Core Rate Limiter | ⬜ 待实施 |
| 断路器 | CircuitBreaker → Polly (Microsoft.Extensions.Resilience) | ⬜ 待实施 |
| 嵌入抽象 | IEmbeddingBackend → IEmbeddingGenerator | ⬜ 待实施 |
| 可观测性 | — → OpenTelemetry + Serilog | ⬜ 待实施 |

## Phase 2: RAG/知识层替换

| 任务 | 自研 → 替换 | 备注 |
|------|-----------|------|
| 文档存储 | DocumentStore (SQLite+FTS5) → Kernel Memory | 多格式支持/分块/ETL |
| 向量存储 | ConcurrentDictionary → LanceDB .NET SDK | 生产级内嵌向量库 |
| 知识检索 | KnowledgeBase → Kernel Memory Search API | 引用过滤/标签/分区 |

## Phase 3: 多智能体编排

| 任务 | 自研 → 替换 | 备注 |
|------|-----------|------|
| 治理层 | 11 Governors → MAF Middleware + Workflow | 状态持久化/重放/人工审批 |
| 编排 | Orchestrator → MAF SequentialWorkflow | 生产级编排 |
| 工具 | CapabilityGovernor → MAF Tool/Function middleware | 标准工具协议 |

## Phase 4: 网络/生产化

| 任务 | 自研 → 替换 | 备注 |
|------|-----------|------|
| P2P通信 | P2PNode → MAF A2A | Agent-to-Agent 标准协议 |
| 消息总线 | Channel\<T\> → MassTransit + RabbitMQ | 企业级消息 |
| 部署 | Program.cs → MAF Hosting | 容器化/自动扩缩容 |
| 可观测性 | — → OpenTelemetry (完整) | 链路追踪/指标/日志 |

## 老项目缺口对照

详见 `ECOSYSTEM_ANALYSIS.md` 第 12 节。核心未移植模块:
- `dna/` (123 文件): 意识/自进化/安全 — 必须自研
- `capability/` (95 文件): 工具/技能 — 微软生态 + 社区
- `core/` (75 文件): 系统核心 — 大部分可用微软生态替代
- `observability/` (17 文件): OpenTelemetry 全覆盖
- `reasoning/` (10 文件): 自研

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
- All 12 projects: 0 errors, 9 warnings
- Tests: 27 passed, 0 failed
- Target: .NET 10 (running on .NET 11 SDK preview)
