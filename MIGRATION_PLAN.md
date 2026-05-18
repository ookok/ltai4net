# LTAI .NET 迁移进度

> 源仓库: https://github.com/ookok/LivingTreeAlAgent (Python ~700模块)  
> 目标仓库: https://github.com/ookok/ltai4net (NET 10)  
> 更新: 2026-05-18 | Phase 1 进行中

## 当前状态

| 指标 | 值 |
|------|-----|
| 项目数 | 23 |
| .cs 文件 | ~230 |
| 构建 | 0 错误 |
| 测试 | 27 通过 |
| 端点 | 63 |
| 工具 | 14 |
| Phase 1-6 | ✅ 完成 |
| 交付闭环 | ✅ 完成(压测+AOT) |
| Phase 1-6 | ✅ 完成 |

## 阶段路线

| Phase | 目标 | 状态 |
|-------|------|:----:|
| Phase 1 | 基础设施标准化 (IChatClient + Polly + Rate Limiter + OTEL + Serilog) | ✅ 完成 |
| Phase 2 | RAG/知识层替换 (Kernel Memory + IEmbeddingGenerator) | 🟡 基础集成完成 |
| Phase 3 | 多智能体编排 (MAF 集成项目 + Agent/L1 API) | 🟡 LTAI.MAF 项目已创建 |
| Phase 4 | 网络/生产化 (MassTransit + RabbitMQ + A2A + HealthChecks + SmartDNS) | ✅ 完成 |
| Phase 5 | dna/意识/自进化 (LTAI.DNA + 7端点) | ✅ 完成 |

## 技术选型

| 层 | 技术 | 依据 |
|----|------|------|
| 运行时 | .NET 10 | 最高性能，AOT支持 |
| LLM网关 | **Microsoft.Extensions.AI** (IChatClient) | 官方抽象，中间件管道，28厂商兼容 |
| Agent框架 | 自研 CognitiveMesh (11 Governors) + **MAF** (A2A) + **DNA** (意识/进化/人格/安全) | 仿生 + 微软标准协议 |
| Web | ASP.NET Core Minimal API + Rate Limiter + MAF端点 + **DNA端点** | 微软生态 |
| 限流 | **ASP.NET Core Rate Limiter** (替换自研令牌桶) | 内置支持 |
| 韧性 | **Polly** (Microsoft.Extensions.Resilience) | .NET 标准韧性库 |
| 消息总线 | **MassTransit** + RabbitMQ (可选) | 企业级消息 |
| Agent通信 | **A2A** (/api/a2a/message) | Agent-to-Agent 协议 |
| 可观测性 | **OpenTelemetry** + **Serilog** + **HealthChecks** | 全链路追踪 |
| 浏览器 | PuppeteerSharp | Chrome控制 |
| HTML | HtmlAgilityPack | XPath选择器 |
| RAG/知识 | **Kernel Memory** + 自研保留 | 文档导入/语义搜索 |
| 向量嵌入 | **IEmbeddingGenerator** (适配器模式) | M.E.AI 抽象层 |
| 文档解析 | **NPOI** (Office) + **PdfPig** (PDF) + **Markdig** (MD) + UniversalFileParser | 多格式支持 |
| 代码引擎 | **MultiLangCodeAnalyzer** + **Roslyn** + 正则AST (13语言) | C#/Py/JS/TS/Go/Rust/Java/SQL/HTML |
| 搜索 | **UnifiedSearchEngine** (DuckDuckGo + Wikipedia + MDN) | 多源聚合 |
| DB | Microsoft.Data.Sqlite + FTS5 | 全文搜索 |
| 序列化 | System.Text.Json | AOT友好 |
| P2P | Channel\<NetworkMessage\> + **SmartDNS** + ProxyPool | 异步消息 + DNS缓存 |
| AOT编译 | **NativeAOT** (PublishAot) 单文件部署 | 独立exe |
| 测试 | xUnit + **BenchmarkDotNet** | 性能基准 |

## 模块映射

### ✅ 已完成 (12 项目)

| Python 子系统 | .NET 项目 | .cs | 状态 |
|---------------|-----------|-----|------|
| `core/interfaces` | `LTAI.Core` | 15 | ✅ 接口层+配置+EIAModels+ContextFolding |
| `treellm/governors` | `LTAI.AI` | 16 | ✅ 11 Governors + ProviderEngine + LivingTreeSystem + GovernorUtilities |
| `api/` | `LTAI.Web` | 3 | ✅ 3端点 + 令牌桶限流 → **ASP.NET Core RateLimiter** + **HealthChecks** |
| `knowledge/` | `LTAI.Vector` | 17 | ✅ VectorStore+Embedding+DocumentStore+KnowledgeBase+AgenticRAG+Reranker+KG+StructMem+**KernelMemoryStore**+**IEmbeddingGenerator** |
| `capability/browser` | `LTAI.Browser` | 5 | ✅ PuppeteerSharp + AdaptiveExtractor |
| `capability/parser` | `LTAI.Document` | 6 | ✅ UniversalFileParser + 9 parsers |
| `reasoning/` | `LTAI.Capability` | 9 | ✅ MathReasoner + FormalLogicEngine + DialecticalReasoner + AttributionReasoner + ReasoningOrchestrator + **已接入主流程增强回复** |
| `capability/code+search+doc` | `LTAI.Capability` | — | ✅ MultiLangCodeAnalyzer (13语言) + UnifiedSearchEngine + DocumentProcessor (NPOI+PdfPig+Markdig) |
| `network/` | `LTAI.Network` | 8 | ✅ P2PNode + ServiceDiscovery + **MassTransitMessageBus** + **SmartDnsResolver** + **ProxyPool** + gRPC |
| `treellm/routing` | `LTAI.TreeLLM` | 10 | ✅ 6路由策略 + 14维HolisticElection + ElectionBus + CircuitBreaker(**DI可注册**) + CoherenceGate + SemanticCache + AutoPrompt + StrategicDistiller |
| `execution/` | `LTAI.Execution` | 10 | ✅ TaskTree + ReactExecutor(47工具) + DAGExecutor + BatchExecutor + Orchestrator + QualityChecker(9步) + QualityScorer + SelfHealer + RecursiveDecomposer |
| `memory/` | `LTAI.Memory` | 7 | ✅ UserModel(L1-L3) + PersonaMemory + EmotionalMemory(Plutchik) + MemoryPolicy(MemPO) + MemoryOrchestrator + TraitEvolution |
| `economy/` | `LTAI.Economy` | 5 | ✅ Metabolism(12器官) + EconomicEngine(ROI+合规) + ThermoBudget(KL级联) + GRPO(3合1) + InverseReward |
| — | `LTAI.MAF` | 6 | 🟡 Agent API + /api/maf/* + /api/a2a/* + Input/OutputFilter + **A2AHost** |
| `dna/` | `LTAI.DNA` | 7 | ✅ **已接入主流程**: 安全意识审查 + 意识处理 + 输出安全 + 进化反馈 + /api/status DNA状态
| — | `LTAI.MCP` | 3 | ✅ MCP Server: JSON-RPC 2.0 + tools/list + tools/call + resources/list + resources/read + StdioTransport + 自动发现 |
| — | `LTAI.Host` | 1 | ✅ Program.cs + appsettings.json |

### 🟡 待迁移

| Python 子系统 | 文件 | 优先级 |
|---------------|------|--------|
| `dna/` | 124 | 🔴 高 (意识/自进化/安全/激素/生命引擎) |
| `capability/` deep | 80+ | 🟡 中 (代码引擎/搜索/技能/管道) |
| `reasoning/` | 30+ | 🟡 中 |
| `observability/` | 18 | 🟡 中 |
| `integration/` | 16 | 🟢 低 |
| `optimization/` | 4 | 🟢 低 |
| `serialization/` | 5 | 🟢 低 |

### ❌ 计划复用 (第三方)

| 自研组件 | → 替换方案 |
|----------|-----------|
| IProviderEngine | Microsoft.Extensions.AI `IChatClient` |
| IVectorStore | Microsoft.Extensions.VectorData |
| DocumentStore | Kernel Memory |
| LivingTreeSystem | MAF AIAgent + Workflow |
| CircuitBreaker | Polly |
| RateLimiting | ASP.NET Core Rate Limiter |

## 覆盖率

整体 ~25% Python 代码库已 .NET 化。核心治理+知识检索+浏览器+文档解析+执行引擎+记忆层+经济层+路由引擎已就位。DNA(意识/进化/安全)为下一波重点。
