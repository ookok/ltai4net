# .NET AI 生态分析 & LTAI4Net 迁移复用评估

> 生成日期: 2026-05-18 | 更新: 深入调研版

## 1. 生态全景图 (2026年5月最新)

### 微软官方 AI 全家桶

| 组件 | 状态 | 定位 | 对标 Python |
|------|:--:|------|------------|
| **Microsoft.Extensions.AI** | ✅ Stable | 核心抽象层: `IChatClient` + `IEmbeddingGenerator` + `IImageGenerator`。内置中间件管道: OpenTelemetry/Cache/FunctionInvocation/Logging | `IProviderEngine` + `IEmbeddingBackend` |
| **Microsoft Agent Framework v1.0** | ✅ Stable (2026-04) | 生产级多智能体编排。支持: Sequential/Concurrent/Handoff/Group workflows + CodeAct(50%延迟降低) + A2A/MCP + AG-UI + 技能系统 + Checkpointing + Human-in-the-Loop + OTEL + Foundry部署 | `LivingTreeSystem` + `Orchestrator` |
| **Semantic Kernel** | ✅ Active | MAF 基础层, 插件系统 (`IToolRegistry` 对标), ChatHistory, Planner | Plugin + Memory |
| **Kernel Memory** | ✅ Active | RAG/记忆方案: 多格式文档导入 + 智能分块 + 向量存储(Qdrant/Azure/Postgres/Redis/ES) + 语义搜索 + Web Service | `DocumentStore` + `VectorStore` |
| **Microsoft.ML.Tokenizers** | ✅ Stable | 高性能文本分词器 (Tiktoken/BPE/WordPiece) | `tiktoken` |
| **ML.NET** | ✅ Stable | 传统ML/AutoML | `scikit-learn` |
| **Agent Governance Toolkit** | ✅ New | 运行时策略执行、代理行为管控、端到端审计 | `SafetyCoordinator` |
| **Foundry Agent Service** | ✅ New | 容器化Agent部署: 身份认证、自动扩缩容、会话状态管理、可观测性、版本管理 | Docker/K8s 部署 |
| **CodeAct** | 🧪 Alpha | 单代码块替代多步工具调用 (降延迟50%, 降token 60%), Hyperlight沙箱隔离 | `ReactExecutor` |
| **Microsoft.Extensions.VectorData** | ✅ New | 统一向量存储抽象 (InMemory/Qdrant/Azure/Redis/Pinecone) | `VectorStore` |
| **Microsoft.Extensions.DataIngestion** | ✅ New | RAG 数据预处理管道 (分块/清洗/增强) | `knowledge/pipeline` |
| **Windows AI / Foundry Local** | ✅ Active | WinML/DirectML 硬件加速推理 + 本地优先模型执行 | `HardwareAccelerator` |

### 社区/第三方

| 项目 | 状态 | 定位 |
|------|:--:|------|
| **LangChain.NET** | 🟡 Active | Python LangChain 官方C#移植 (ReAct/Chain/工具调用/向量存储) |
| **AgentFlow** | 🟡 Active | 轻量级状态机Agent框架 (复杂任务分步执行、状态管理、错误重试) |
| **Ollama.NET Agent** | 🟡 Active | 本地私有化Agent (LLaMA3/Mistral/Phi, 无需联网) |
| **LlamaSharp** | ✅ Active | 本地大模型推理库 (LLaMA 2/3, Mistral, Phi) |
| **AntSK** | 🟡 Active | 国产知识库平台 (.NET 9 + Blazor + SK + Kernel Memory + Ollama) |
| **BotSharp** | 🟡 Active | .NET 多智能体框架 (插件+多Agent路由/规划+RAG+MCP+WebSocket)
| **AForge.NET** | 🟡 Active | 计算机视觉/图像处理/神经网络/遗传算法 (`scikit-image`+`deap` 对标)

```
┌─────────────────────────────────────────────────────────────────┐
│                    .NET AI 生态架构                              │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  ┌──────────────────────────────────────────────────┐           │
│  │  Microsoft Agent Framework (MAF) ★推荐           │           │
│  │  生产级多智能体编排 | 图工作流 | 10.5k⭐          │           │
│  │  NuGet: Microsoft.Agents.AI                      │           │
│  └────────────────────┬─────────────────────────────┘           │
│                       │                                          │
│  ┌────────────────────┴─────────────────────────────┐           │
│  │  Microsoft.Extensions.AI (抽象层)                 │           │
│  │  IChatClient / IEmbeddingGenerator / IImageGenerator         │
│  │  NuGet: Microsoft.Extensions.AI.Abstractions     │           │
│  └──────┬──────────────────────┬───────────────────┘           │
│         │                      │                                 │
│  ┌──────┴──────┐    ┌──────────┴──────────┐                    │
│  │ Semantic    │    │ Kernel Memory        │                    │
│  │ Kernel      │    │ (RAG/记忆方案)       │                    │
│  │ 插件/记忆    │    │ 2.2k⭐ 研究项目     │                    │
│  │ 规划器       │    │ NuGet: Microsoft.   │                    │
│  │             │    │   KernelMemory.Core  │                    │
│  └─────────────┘    └─────────────────────┘                    │
│                                                                  │
│  ┌──────────────────────────────────────────────────┐           │
│  │  社区/开源方案                                    │           │
│  ├──────────────────────────────────────────────────┤           │
│  │  BotSharp (3.1k⭐) - .NET 多智能体框架           │           │
│  │  AutoGen.NET (58k⭐) - 维护模式, MAF 代替        │           │
│  │  AntSK (1.3k⭐) - 国产知识库/智能体, SK+KM      │           │
│  │  LangChain.NET - 组件化 LLM 框架                 │           │
│  │  LlamaSharp - 本地模型推理                       │           │
│  │  OllamaSharp - Ollama .NET SDK                   │           │
│  └──────────────────────────────────────────────────┘           │
└─────────────────────────────────────────────────────────────────┘
```

## 2. Microsoft Agent Framework (MAF) — 核心推荐

| 特性 | 说明 | 对标 Python |
|------|------|-------------|
| **Agent** | AIAgent 基类, 支持 instructions/name/description | TreeLLM Agent |
| **Middleware** | 请求/响应/异常处理管道 | LayerGovernor 体系 |
| **Workflows** | 图工作流: Sequential/Concurrent/Handoff/Group | LivingTreeSystem + Orchestrator |
| **Checkpointing** | 工作流状态持久化 | TaskJournal |
| **Streaming** | 内置流式支持 | SSE in ProviderEngine |
| **Human-in-the-Loop** | 人工审批节点 | hitl.py |
| **OpenTelemetry** | 分布式追踪 | observability/ |
| **A2A/MCP** | Agent-to-Agent, Model Context Protocol | network/gRPC |
| **Declarative Agents** | YAML 定义 Agent | — |
| **Agent Skills** | 多源知识库集成 | knowledge/ |

**NuGet 包:**
- `Microsoft.Agents.AI` — 核心 Agent 抽象
- `Microsoft.Agents.AI.Foundry` — Microsoft Foundry 集成
- `Microsoft.Agents.AI.OpenAI` — OpenAI 集成

## 3. Microsoft.Extensions.AI — 基础抽象层

### IChatClient 接口
```csharp
// 对标我们的 IProviderEngine
public interface IChatClient : IDisposable
{
    Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    ChatClientMetadata Metadata { get; }
    object? GetService(Type serviceType, object? key = null);
}
```

### IEmbeddingGenerator 接口
```csharp
// 对标我们的 IEmbeddingBackend
public interface IEmbeddingGenerator<TInput, TEmbedding> : IDisposable
{
    Task<GeneratedEmbeddings<TEmbedding>> GenerateAsync(
        IEnumerable<TInput> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default);

    EmbeddingGeneratorMetadata Metadata { get; }
    object? GetService(Type serviceType, object? key = null);
}
```

**内置中间件管道:**
- `UseOpenTelemetry()` — OTEL 集成
- `UseDistributedCache()` — 分布式缓存
- `UseFunctionInvocation()` — 自动函数调用
- `UseLogging()` — 结构化日志

## 4. Semantic Kernel — LLM 应用 SDK

| 概念 | 说明 | 对标 |
|------|------|------|
| **Kernel** | 核心编排器, 管理插件/服务/记忆 | LivingTreeSystem |
| **Plugin** | 可调用函数集合 (Native/OpenAPI) | IToolRegistry + capability/ |
| **KernelFunction** | 单个可调用函数 | IToolRegistry handler |
| **KernelPlugin** | 函数集合 | tool_market.py |
| **ChatHistory** | 对话历史管理 | ContextGovernor |
| **HandlebarsPlanner** | 模板化任务规划 | TaskPlanner |
| **StepwisePlanner** | 逐步推理规划 | ReActExecutor |
| **MemoryPlugin** | 向量记忆 | VectorStore |
| **Process Framework** | 业务流程自动化 | Orchestrator |

## 5. Kernel Memory — RAG/记忆

| 特性 | 说明 | 对标 |
|------|------|------|
| **Document Ingestion** | 多格式文档导入 (PDF/Office/Web) | DocumentStore.AddDocument |
| **Text Partitioning** | 智能分块 | SplitChunks |
| **Embedding Generation** | 向量生成 | IEmbeddingBackend |
| **Vector Storage** | 多后端 (Qdrant/Azure/Postgres/Redis/Elasticsearch) | VectorStore |
| **Semantic Search** | 语义检索 | SearchSimilar |
| **Memory Web Service** | REST API | Web API |
| **Pipelines** | 可组合处理管道 | pipeline_engine |

## 6. 功能对照表: Python → 已有 .NET 实现 → 推荐复用

### A. 必须保留自研的部分 (核心差异化)

| Python 模块 | 原因 | 策略 |
|-------------|------|------|
| **dna/** — 意识/情绪/自进化 | 无成熟替代品, 是 LivingTree 的核心创新 | 继续自研, 独立维护 |
| **记忆层** — EmotionalMemory, UserModel, PersonaMemory | 个性化用户建模, 情绪感知 | 自研, 作为 MAF Middleware |
| **environment** — EIAModels 56 个环境模型 | 行业专用 (EIA/环评), 无开源替代 | 自研, 独立 NuGet 包 |
| **ContextFolding, TaskJournal** | 核心独创算法 (arxiv) | 自研, MAF 中间件集成 |
| **ContextGovernor 知识预加载** | 场景化上下文注入 | 自研 |
| **HolisticElection** — 14 维评分选举 | 多维度 LLM 选择算法 | 自研 |

### B. 可以替换的部分 (用成熟组件)

| Python 模块 | 当前 .NET 实现 | 推荐替换方案 | 收益 |
|-------------|---------------|-------------|------|
| **IProviderEngine** (10 行接口) | 自研 ProviderEngine (~200 行) | → **Microsoft.Extensions.AI.IChatClient** | 免费获得 OTEL/Cache/Middleware |
| **IEmbeddingBackend** | 自研 LocalEmbeddingBackend | → **Microsoft.Extensions.AI.IEmbeddingGenerator** | 统一抽象 |
| **VectorStore** | 自研 ConcurrentDictionary | → **Kernel Memory** 或 Qdrant 直接集成 | 生产级存储 |
| **DocumentStore** | 自研 SQLite+FTS5 | → **Kernel Memory MemoryBuilder** | 多格式支持/分块/ETL |
| **KnowledgeBase** | 自研编排器 | → **Kernel Memory** 检索 API | 引用过滤/标签/分区 |
| **整个 LTAI.AI 治理层** | 11 个 Governor + LivingTreeSystem | → **MAF Workflows** (图工作流) | 状态持久化/重放/人工审批 |
| **Orchestrator** | 自研 TaskSpec/SubTask | → **MAF SequentialWorkflow/ConcurrentWorkflow** | 生产级编排 |
| **observability/** | 0% 覆盖 | → **Microsoft.Extensions.AI + OpenTelemetry** | 零代码获取追踪 |
| **api/** | 3 个端点 | → **MAF Hosting** (A2A/Durable) | 企业级部署 |
| **circuit_breaker** | 自研 CircuitBreaker | → **Polly** (Microsoft.Extensions.Resilience) | 更成熟 |
| **BrowserAgent (PuppeteerSharp)** | 自研 3 层导航 | → **MAF + Playwright MCP** | MCP 标准协议 |
| **网络层 gRPC/P2P** | 自研 Channel<T> | → **MAF A2A** (Agent-to-Agent 协议) | 标准互操作 |
| **RateLimitingMiddleware** | 自研令牌桶 | → **ASP.NET Core 内置 Rate Limiter** | 内置支持 |
| **Config 管理** | LTAIOptions | → **Microsoft.Extensions.Configuration** + Options 模式 | 标准实践 |

### C. 可参考学习的部分

| 项目 | 学习重点 | 复用点 |
|------|---------|--------|
| **BotSharp** | 插件加载器, 多 Agent 路由, 对话状态管理 | 插件体系架构参考 |
| **AntSK** | SK + KM 集成实践, 国产模型适配, 知识库 UI | LLM provider 适配代码 |
| **AutoGen.NET** | 事件驱动 Agent, 分布式 Runtime, gRPC 通信 | Core/Contracts 包可复用 |

## 7. 迁移路线图 (四阶段)

### Phase 1: 抽象层升级 (1-2 周)

```
当前                      →  目标
─────────────────────────────────────
IProviderEngine            →  Microsoft.Extensions.AI.IChatClient
IEmbeddingBackend           →  Microsoft.Extensions.AI.IEmbeddingGenerator
CircuitBreaker              →  Polly (Microsoft.Extensions.Resilience)
RateLimitingMiddleware      →  ASP.NET Core Rate Limiter
```

**具体步骤:**
1. 添加 `Microsoft.Extensions.AI.OpenAI` NuGet 包
2. 创建适配器: `ProviderEngine → IChatClient` 封装
3. 替换 `IProviderEngine` 注入为 `IChatClient`
4. `GovernorUtilities` 中的 LLM 调用切换为 `IChatClient.GetResponseAsync()`
5. 自动获得: 日志中间件、OTEL 遥测、缓存中间件

### Phase 2: RAG/知识层替换 (2 周)

```
当前                      →  目标
─────────────────────────────────────
DocumentStore (SQLite+FTS5) →  Kernel Memory + Qdrant
VectorStore (ConcurrentDict) →  Kernel Memory Vector Store
KnowledgeBase               →  Kernel Memory Search API
LocalEmbeddingBackend       →  IEmbeddingGenerator (OpenAI/BGE)
```

**具体步骤:**
1. 添加 `Microsoft.KernelMemory.Core` NuGet
2. 配置 Qdrant 向量数据库
3. 用 `MemoryServerless.ImportDocumentAsync()` 替换 `DocumentStore.AddDocument()`
4. 用 `MemoryServerless.SearchAsync()` 替换 `KnowledgeBase.Search()`
5. 保留 `EIAModels`、`KnowledgeGraph`、`RelationEngine` 作为独立模块

### Phase 3: 多智能体编排 (3 周)

```
当前                      →  目标
─────────────────────────────────────
LivingTreeSystem (11 Gov)  →  MAF AIAgent + Workflow
Orchestrator               →  MAF SequentialWorkflow
CapabilityGovernor         →  MAF Tool/Function middleware
ContextGovernor            →  自研 middleware (差异化)
```

**具体步骤:**
1. 添加 `Microsoft.Agents.AI` + `Microsoft.Agents.AI.OpenAI` NuGet
2. 每个 Governor 重构为 MAF Middleware 或独立 Agent
3. `LivingTreeSystem.ChatAsync()` 替换为 MAF Workflow
4. `ContextGovernor.PreloadKnowledgeAsync()` 保留为自定义 Middleware
5. 保留: EmotionalMemory、UserModel、PersonaMemory (注入 MAF Context)
6. 保留: HolisticElection (作为 MAF 的 Provider 选择策略)

### Phase 4: 生产化 (2 周)

```
当前                      →  目标
─────────────────────────────────────
observability/ (0%)        →  OpenTelemetry (内置)
api/ (3 端点)               →  MAF Hosting + ASP.NET Core
Network/P2P                →  MAF A2A 协议
Config 管理                 →  IConfiguration + Options 模式
```

**具体步骤:**
1. 启用 ASP.NET Core OpenTelemetry 导出
2. 基于 MAF Hosting 模式部署
3. 保留: `EIAModels`、`SelfHealer`、`EmotionalMemory` 作为 MAF Skill/Tool
4. 删除: `LTAI.AI` 中的 ProviderEngine (已被 IChatClient 替代)
5. 删除: `LTAI.Vector` 中的 DocumentStore/VectorStore (已被 Kernel Memory 替代)
6. 删除: `LTAI.Network` (被 MAF A2A 替代)

## 8. 最终架构 (Phase 4 后)

```
┌──────────────────────────────────────────────────────────────┐
│  LTAI.Host (ASP.NET Core)                                    │
│  ├── MAF AIAgent (多智能体编排)                               │
│  │   ├── Middleware: ContextPreloader (自研)                 │
│  │   ├── Middleware: EmotionDetector (自研)                  │
│  │   ├── Middleware: PersonaInjector (自研)                  │
│  │   └── Middleware: OpenTelemetry (内置)                    │
│  ├── IChatClient (Microsoft.Extensions.AI)                   │
│  ├── Kernel Memory (RAG: 文档导入/向量检索)                  │
│  ├── LTAI.EIAModels (自研: 56 环境模型)                      │
│  ├── LTAI.TreeLLM (自研: HolisticElection/ForesightGate)    │
│  ├── LTAI.Memory (自研: UserModel/Emotion/Persona)           │
│  └── LTAI.Execution (自研: QualityChecker/SelfHealer)        │
└──────────────────────────────────────────────────────────────┘
```

### 代码量估算

| 组件 | 当前 (.cs 文件) | Phase 4 后 | 来源 |
|------|:---:|:---:|------|
| LTAI.Host | 1 | 1 | 自研 |
| MAF Agent 配置 | 0 | 2-3 | 配置代码 |
| IChatClient | — | 0 | NuGet |
| Kernel Memory | — | 0 | NuGet |
| LTAI.TreeLLM (Election/Foresight) | 7 | 5 | 自研 |
| LTAI.Execution (Quality/SelfHeal) | 8 | 6 | 自研 |
| LTAI.Memory (User/Emotion/Persona) | 6 | 6 | 自研 |
| LTAI.Core (Mesh/Journal/EIA/Context) | 5 | 4 | 自研 |
| **删除** LTAI.AI (11 Gov + Provider) | 14 | 0 | — |
| **删除** LTAI.Vector (17 files) | 17 | 0 | — |
| **删除** LTAI.Network (5 files) | 5 | 0 | — |
| **删除** LTAI.Browser (5 files) | 5 | 0 | MCP 替代 |
| **删除** LTAI.Document (6 files) | 6 | 0 | Kernel Memory 替代 |
| **删除** LTAI.Web (3 files) | 3 | 0 | MAF Hosting 替代 |
| **总计** | **84** | **~27** | **减少 68%** |

## 9. 技术决策总结

| 决策 | 选择 | 理由 |
|------|------|------|
| LLM 调用抽象 | **Microsoft.Extensions.AI** | MS 官方抽象, 生态兼容, 内置中间件 |
| 多 Agent 编排 | **Microsoft Agent Framework** | 生产级, 微软维护, 图工作流 |
| RAG/知识检索 | **Kernel Memory + Qdrant** | 专业 RAG 方案, 多后端 |
| 韧性/容错 | **Polly** | .NET 标准韧性库 |
| 可观测性 | **OpenTelemetry** (内置) | 零代码集成 |
| 路由选举 | **自研** (HolisticElection) | 14 维 + LPO + Ising Model, 无替代 |
| 环境模型 | **自研** (EIAModels) | 行业专用, 56 函数 |
| 用户记忆 | **自研** (Memory 层) | 个人化建模, 情绪感知 |
| 质量检查 | **自研** (QualityChecker) | 9 步流水线, 多跳证据检查 |
| 浏览器 | **Playwright MCP server** | MCP 标准协议 |

---

## 10. 推荐 NuGet 包与选型索引

### 基础设施与核心能力

| 包 | 用途 | 当前状态 |
|---|------|----------|
| **System.Text.Json** | JSON 序列化 (已使用) | ✅ |
| **Serilog** | 结构化日志 (替换 `ILogger<T>` 后端) | ⚠️ 待集成 |
| **Polly** | 重试/断路器/超时/回退 (替换 `CircuitBreaker.cs`) | ❌ 待引入 |
| **AutoMapper** | 对象映射 (替换 `MapDocument()` 等手写代码) | ⚠️ 可引入 |
| **FluentValidation** | 数据验证 (替换 Data Annotations) | ❌ 可选 |
| **MediatR** | CQRS 中介者 (10 个 Governor 天然适合) | ❌ 可选 |
| **Swashbuckle.AspNetCore** | Swagger/OpenAPI 文档 | ❌ LTAI.Web 已用 MapEndpoints |
| **StackExchange.Redis** | 分布式缓存/会话 | ❌ 待引入 |

### 爬虫反爬体系

| 包 | 用途 | 当前状态 |
|---|------|----------|
| **HtmlAgilityPack** | 容错 HTML 解析 + XPath/CSS 选择器 | ✅ LTAI.Browser 已使用 |
| **PuppeteerSharp** | 无头 Chrome 控制 (SPA 渲染/登录) | ✅ LTAI.Browser 已使用 |

.NET 反爬公式: `PuppeteerSharp (真实浏览器) + 指纹噪声注入 + 住宅代理 + 随机行为延迟`

### 向量与 AI

| 包 | 用途 | 当前状态 |
|---|------|----------|
| **Microsoft.Extensions.AI** | `IChatClient` + `IEmbeddingGenerator` 抽象层 | ❌ 替换 `IProviderEngine` |
| **Microsoft.Extensions.VectorData** | 统一向量存储抽象 (InMemory/Qdrant/Azure/Redis/Pinecone) | ❌ 替换 `IVectorStore` |
| **LanceDB .NET SDK** | 生产级向量库 (对标 Python LanceDBStore) | ❌ 替换 `ConcurrentDictionary` |
| **ONNX Runtime 1.18+** | CUDA/DirectML AI 推理加速 | ❌ 可选 |
| **ML.NET** | 传统 ML (分类/回归/聚类/AutoML) | ❌ 可选 |
| **Semantic Kernel** | LLM 应用 SDK (插件/记忆/规划器) | ❌ 替换 `IToolRegistry` |
| **Microsoft Agent Framework v1.0** | 多智能体编排 (Sequential/Concurrent/Handoff/Group) | ❌ 替换 `LivingTreeSystem` |

### 代码分析工具

| 包 | 场景 | 对标 Python |
|---|------|-----------|
| **Roslyn** (`Microsoft.CodeAnalysis`) | C#/VB.NET 深度分析 (`SyntaxTree` + `SemanticModel`) | `ast.parse` |
| **Tree-sitter** (Graphify-DotNet) | 跨语言 AST 解析 (Python/JS/TS/Go/Rust等) | `tree-sitter` |
| **Parlot** | 自定义语法/表达式解析器 | `lark` / `pyparsing` |
| **Masuit.Tools** | 反射/树结构/常用工具封装 | — |
| **Microsoft.CodeAnalysis.NetAnalyzers** | 代码质量实时诊断 (性能/安全/API) | `pylint` / `ruff` |

### UI 框架

| 包 | 场景 | 对标 Python |
|---|------|-----------|
| **Spectre.Console** | 终端 TUI 仪表板 | `textual` (`tui/DevTUI`) |
| **Avalonia UI** | 跨平台桌面端 | `PyQt6` (`client/`) |
| **Blazor** | Web 实时交互 (替代 Jinja2+HTMX) | `templates/` |

### 网络与通信

| 包 | 用途 | 对标 Python / 当前状态 |
|---|------|-----------------------|
| **linker** | P2P 打洞 + 虚拟组网 | 替换 `P2PNode.cs` (NAT穿透) |
| **FreeIM** | 聊天/消息推送 | 替换 `message_bus` + `im_core` |
| **gRPC** (`Grpc.AspNetCore` + `Google.Protobuf`) | 高性能 RPC 服务 | ✅ LTAI.Network 已使用 |
| **OpenTelemetry** | 链路追踪/指标/日志 | ❌ 待引入 (MAF 内置) |

### 文档解析

| 包 | 用途 | 对标 Python |
|---|------|-----------|
| **PdfPig** | PDF 文本/结构解析 | `pypdf` / `pdfplumber` |
| **Tabula.DotNet** | 原生 PDF 表格提取 (快/精度100%) | `pdfplumber` + `camelot` |
| **PaddleOCRSharp** | OCR 视觉表格识别 (扫描件回退, 中文优化) | `paddleocr` + `pytesseract` |
| **NPOI** | Office 文档 (.xls/.xlsx/.docx) | `openpyxl` + `python-docx` |
| **Markdig** | Markdown 解析 (CommonMark + 扩展) | `mistune` / `markdown-it` |
| **DocumentFormat.OpenXml** | Office Open XML SDK | `python-pptx` |

**PDF 表格路由架构:** PdfPig/Tabula.DotNet (原生提取优先) → 判定为扫描件 → PaddleOCRSharp OCR 回退

### 整合建议

- **已使用并保留**: `System.Text.Json`, `HtmlAgilityPack`, `PuppeteerSharp`, `gRPC+Protobuf`
- **优先替换**: `IProviderEngine` → `Microsoft.Extensions.AI.IChatClient`, `CircuitBreaker` → `Polly`
- **渐进升级**: `IVectorStore` → `VectorData` 抽象, `SQLite` → `LanceDB .NET SDK`
- **待 RTM**: 全部 `net10.0` → `net11.0` 升级, 引入 `ONNX Runtime` / `ML.NET`
- **保留自研**: `HolisticElection` (14维选举), `EIAModels` (56环境函数), `ContextFolding`, `Memory` 层 (User/Emotion/Persona), `QualityChecker` (9步流水线)
