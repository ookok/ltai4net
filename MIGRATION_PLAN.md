# LTAI .NET 迁移进度

> 源仓库: https://github.com/ookok/LivingTreeAlAgent (Python ~700模块)  
> 目标仓库: https://github.com/ookok/ltai4net (.NET 10)  
> 更新: 2026-05-19 | 整体进度 ~80%

## 当前状态

| 指标 | 值 |
|------|-----|
| 项目数 | 23 (16 入 .sln + 7 待加入) |
| .cs 文件 | ~371 |
| 构建 | 0 错误, 18 警告 |
| 端点 | 193+ |
| 整体覆盖率 | ~83% (Python→.NET) |

## 阶段路线

| Phase | 目标 | 状态 |
|-------|------|:----:|
| Phase 1 | 基础设施标准化 (IChatClient + Polly + Rate Limiter + OTEL + Serilog) | ✅ 完成 |
| Phase 2 | RAG/知识层替换 (Kernel Memory + IEmbeddingGenerator) | ✅ 完成 |
| Phase 3 | 多智能体编排 (MAF + Agent/A2A API + 路由深度) | ✅ 完成 |
| Phase 3b | TreLLM 深层路由 (健康预测/推理预算/会话3件套/对抗4件套/智能3件套) | ✅ 完成 |
| Phase 3c | TreLLM 剩余深层 (连接池/连续意识/流体集体/自由池/KV压缩/数据价值密度/自改进器/调试循环/错误拦截) | ✅ 完成 |
| Phase 4 | 网络/生产化 (MassTransit + RabbitMQ + HealthChecks + SmartDNS) | ✅ 完成 |
| Phase 5 | DNA/意识/自进化/安全 (LTAI.DNA 8组件 + 6端点) | ✅ 基础完成 |
| Phase 5b | DNA 深层进化 (世界模型/心智旅行/哥德尔/多人格/意识涌现/元认知) | ✅ 完成 |
| Phase 5c | DNA 剩余深层 (激素信号/生物节律/免疫系统/身份叙事/人格/上下文工程/局部智能/活性存在) | ✅ 完成 |
| Phase 6 | 能力层深化 (GIS/代码审查/搜索/文档/TUI/沙箱) | ✅ 完成 |
| Phase 6b | 能力层深层 (技能系统/工具市场/管道引擎/代码图谱/爬虫/文档引擎/知识觅食) | ✅ 完成 |
| Phase 7a | 执行层深层 (扩散计划/GTSM/检查点/思考进化/成本感知/CoFEE/景观/排名监控/Thompson委派/HITL/会话/压缩/规则池/澄清器) | ✅ 完成 |
| Phase 7b | 网络层深层 (分布式意识/蜂群/NAT/加密通道/生物识别/空间感知/P2P感知/桥梁/弹性/外部访问) | ✅ 完成 |
| Phase 7c | 核心层深层 (硬件加速/数字孪生/行为树/自生长/突触塑性/弹性脑/Shell/屏障/资源树/扫描器/原子修改) | ✅ 完成 |
| Phase 7d | 观测层深层 (评估框架/EFV/统计验证/审计日志/动态策略/系统监控/代码清单) | ✅ 完成 |
| Phase 7b | 网络层深层 (分布式意识/蜂群/集体/NAT穿透/加密信道/二进制协议/信誉/离线模式/生物识别/空间感知/P2P存在/跨设备网关/频道桥/网络韧性/外部访问) | ✅ 完成 |
| Phase 7c | 核心层深层 (GPU加速/数字孪生/行为树/自治增长/突触可塑性/韧性脑/系统健康/Shell/提示盾/资源树/通用扫描/原子修改/异步磁盘/DPO偏好/并发守卫) | ✅ 完成 |
| Phase 7 | 客户端 (MAUI桌面 + Blazor WebApp) | 🟡 基础完成 |

## 技术选型

| 层 | 技术 | 依据 |
|----|------|------|
| 运行时 | .NET 10 | 最高性能，AOT支持 |
| LLM网关 | Microsoft.Extensions.AI (IChatClient) | 官方抽象，中间件管道，28厂商兼容 |
| Agent框架 | 自研 CognitiveMesh + MAF (A2A) + DNA (意识/进化/人格/安全) | 仿生 + 微软标准协议 |
| Web | ASP.NET Core Minimal API + Rate Limiter | 微软生态 |
| 限流 | ASP.NET Core Rate Limiter (替换自研令牌桶) | 内置支持 |
| 韧性 | Polly (Microsoft.Extensions.Resilience) | .NET 标准韧性库 |
| 消息总线 | MassTransit + RabbitMQ (可选) | 企业级消息 |
| Agent通信 | A2A (/api/a2a/message) | Agent-to-Agent 协议 |
| 可观测性 | OpenTelemetry + Serilog + HealthChecks | 全链路追踪 |
| 浏览器 | PuppeteerSharp | Chrome控制 |
| HTML | HtmlAgilityPack | XPath选择器 |
| RAG/知识 | Kernel Memory + 自研保留 | 文档导入/语义搜索 |
| 向量嵌入 | IEmbeddingGenerator (适配器模式) | M.E.AI 抽象层 |
| 文档解析 | NPOI (Office) + PdfPig (PDF) + Markdig (MD) | 多格式支持 |
| 代码引擎 | MultiLangCodeAnalyzer + Roslyn + 正则AST (13语言) | C#/Py/JS/TS/Go/Rust/Java/SQL/HTML |
| 搜索 | UnifiedSearchEngine (DuckDuckGo + Wikipedia + MDN) | 多源聚合 |
| DB | Microsoft.Data.Sqlite + FTS5 | 全文搜索 |
| 序列化 | System.Text.Json | AOT友好 |
| P2P | Channel<NetworkMessage> + SmartDNS + ProxyPool | 异步消息 + DNS缓存 |
| AOT编译 | NativeAOT (PublishAot) 单文件部署 | 独立exe |
| 测试 | xUnit + BenchmarkDotNet | 性能基准 |

## 模块映射

### ✅ 已完成

| Python 子系统 | .NET 项目 | .cs | 状态 |
|---------------|-----------|-----|------|
| `core/interfaces` | LTAI.Core | 16 | ✅ 接口层+配置+EIAModels+ContextFolding+ChatClientExtensions |
| `treellm/governors` | LTAI.AI | 16 | ✅ 11 Governors + ProviderEngine + LivingTreeSystem + GovernorUtilities |
| `api/` | LTAI.Web | 3 | ✅ ASP.NET Core API端点 + RateLimiter + HealthChecks |
| `knowledge/` | LTAI.Vector | 17 | ✅ VectorStore+Embedding+DocumentStore+KnowledgeBase+AgenticRAG+Reranker+KG+StructMem+KernelMemoryStore+IEmbeddingGenerator |
| `capability/browser` | LTAI.Browser | 5 | ✅ PuppeteerSharp + AdaptiveExtractor |
| `capability/parser` | LTAI.Document | 6 | ✅ UniversalFileParser + 9 parsers |
| `reasoning/` | LTAI.Capability | 4 | ✅ MathReasoner + FormalLogicEngine + DialecticalReasoner + ReasoningOrchestrator |
| `capability/` | LTAI.Capability + Browser + Document | 48 | ✅ Skills(3)+Tools(4)+PipelineEngine+CodeGraph+LightCrawler+Evolution(3)+DocEngine(3)+KnowledgeForager + MultiLangCodeAnalyzer + UnifiedSearchEngine + DocumentProcessor + GIS(4地图) + CodeReviewEngine + ReasoningEngine + GatewayServices + BrowserAgent + AdaptiveExtractor + 22新端点 |
| `network/` | LTAI.Network | 8 | ✅ P2PNode + ServiceDiscovery + MassTransitMessageBus + SmartDnsResolver + ProxyPool + gRPC |
| `treellm/routing` | LTAI.TreeLLM | 31 | ✅ 6路由 + Elo+LPO+CSRL + HolisticElection + HealthPredictor + ReasoningBudget + Session(7: Binding/Compressor/Bridge/ConnectionPool/ContinuousConsciousness/FreeModelPool/SegmentedKV/DataValueDensity) + SelfImprover + Adversarial(4) + Intelligence(5: ThreeModel/L1L2/FluidCollective/Accel) + Resilience(2: DebugLoop/ErrorInterceptor) + CircuitBreaker + SemanticCache + AutoPrompt + StrategicDistiller + ForesightGate |
| `execution/` | LTAI.Execution | 30 | ✅ TaskTree + ReactExecutor(47工具) + DAGExecutor + BatchExecutor + Orchestrator + QualityChecker(9步) + QualityScorer + SelfHealer + RecursiveDecomposer + DiffusionPlanner + GtsmPlanner + TaskCheckpoint + ThinkingEvolution + CostAware + CoFEECognitiveEngine + FitnessLandscape + RankMonitor + ThompsonDelegator + Clarifier + AutoSkillResolver + SessionManager(3) + TerminalCompressor + GlobalRulePool + 22新端点 |
| `memory/` | LTAI.Memory | 11 | ✅ UserModel(L1-L3) + PersonaMemory + EmotionalMemory(Plutchik) + MemoryPolicy(MemPO) + MemoryOrchestrator + TraitEvolution |
| `economy/` | LTAI.Economy | 6 | ✅ Metabolism(12器官) + EconomicEngine + ThermoBudget + GRPO + InverseReward + EconomyModels |
| `dna/` | LTAI.DNA | 30 | ✅ DNADeep(WorldModel/Predictive/MentalTimeTravel/Godelian/SelfEvol/SwarmEvol/Foresight/Entropy/FocusDilution) + DualConsciousness + PhenomenalConsciousness + ConsciousnessEmergence + EvolutionDriver + LifeEngine + SheshaHeads + PlayEngine + MultiStreamEngine + SurpriseGatedMemory + MetaMemory/MetaOptimizer/MetaStrategy + SafetyCoordinator + ToolRepair/RLVRMonitor/DiffusionBridge + ImmuneDefense + LivingCompiler + HormoneNetwork + BiorhythmEngine + IdentityNarrative + Personality + ContextEngineer + LocalIntelligence + LivingPresence + DNAOrchestrator + 18端点 |
| — | LTAI.MAF | 5 | ✅ Agent API + /api/maf/* + /api/a2a/* + A2AHost |
| — | LTAI.MCP | 4 | ✅ MCP Server: JSON-RPC 2.0 + tools/list + tools/call + resources + StdioTransport |
| — | LTAI.Metrics | 2 | ✅ LTAIMetricsCollector + MetricsExtensions (OTEL) |
| — | LTAI.Multimodal | 3 | ✅ MultimodalEndpoints + Services |
| — | LTAI.Sandbox | 6 | ✅ DockerSandbox + ProcessSandbox + SandboxOrchestrator + SandboxEndpoints |
| — | LTAI.Host | 1 | ✅ Program.cs + appsettings.json + AOT配置 |

### 🟡 待迁移 (未入 .sln)

| Python 子系统 | .py 缺口 | 优先级 |
|---------------|:------:|--------|
| `core/` 深层基础设施 | ~55 | 🟡 中 (硬件加速/TTS/数字孪生/行为树/会话持久化) |
| `treellm/` 深层残余 | ~10 | 🟢 低 (代码工具sast/重放回放集成) |
| `dna/` 深层残余 | ~10 | 🟢 低 (生物节律深度/免疫持久化/上下文持久化) |
| `network/` 深层分布式 | ~23 | 🟡 中 (分布式意识/WebRTC/NAT穿透/信誉/离线) |
| `api/` 深层扩展 | ~19 | 🟢 低 (HTMX/WebSocket/OAuth/审计) |
| `integration/` | ~13 | 🟢 低 (Hub启动器/SMS/自更新) |
| `infrastructure/` | ~23 | 🟢 低 (存储后端/GC/IO优化) |
| `observability/` | ~14 | 🟢 低 (评估框架/审计/统计验证) |
| `templates/` | 12 | 🟢 低 (HTML模板) |
| `cell/` | ~10 | 🟢 低 (训练/梦境) |
| `market/` | ~5 | 🟢 低 (Agent市场) |

### ❌ 计划复用 (第三方/微软生态)

| 自研组件 | → 替换方案 |
|----------|-----------|
| IProviderEngine | Microsoft.Extensions.AI `IChatClient` |
| IVectorStore | Microsoft.Extensions.VectorData |
| DocumentStore | Kernel Memory |
| LivingTreeSystem | MAF AIAgent + Workflow |
| CircuitBreaker | Polly |
| RateLimiting | ASP.NET Core Rate Limiter |

## 下一阶段重点 (Phase 7+/可选)

| 优先级 | 方向 | 说明 |
|:--:|------|------|
| 🟡 | 核心层深层 (~55) | 硬件加速/TTS/数字孪生/行为树/自治增长 |
| 🟡 | 网络层深层 (~23) | 分布式意识/WebRTC/NAT穿透/信誉/离线 |
| 🟢 | 客户端深化 | MAUI/Blazor页面扩展 |
| 🟢 | HITL/LLM集成 | 为规划/进化/调试循环接入 LLM 调用 |
| 🟢 | 持久化集成 | 检查点/会话管理器接入数据库/云存储 |
