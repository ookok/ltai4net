# LTAI v6.0 — Agent Mesh Architecture

基于 Microsoft Agent Framework (MAF) 1.6+ 全面重构

---

## 架构全景

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         LTAI Host Layer (统一入口)                        │
│ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌────────────────┐  │
│ │ Web API  │ │   TUI    │ │   MCP    │ │ Desktop  │ │    WebApp      │  │
│ │ (8080)   │ │(Spectre) │ │(Protocol)│ │ (MAUI)   │ │ (Standalone)   │  │
│ └────┬─────┘ └────┬─────┘ └────┬─────┘ └────┬─────┘ └───────┬────────┘  │
│      └───────────┴──────────────┴──────────────┴────────────────┘         │
│                                    │                                      │
│           ┌────────────────────────▼──────────────────────┐              │
│           │          MAF Hosting & DevUI Layer            │              │
│           │  A2A · DurableTask · DevUI · HealthChecks     │              │
│           └────────────────────────┬──────────────────────┘              │
├────────────────────────────────────┼──────────────────────────────────────┤
│                        Agent Mesh Core                                  │
│  ┌─────────────────────────────────▼──────────────────────────────────┐  │
│  │                     Agent Registry (Declarative YAML)               │  │
│  │  ┌───────────┐ ┌───────────┐ ┌───────────┐ ┌───────────────────┐  │  │
│  │  │ChatAgent  │ │CodeAgent  │ │EIAAgent   │ │ReasoningAgent     │  │  │
│  │  └─────┬─────┘ └─────┬─────┘ └─────┬─────┘ └────────┬──────────┘  │  │
│  │        └──────────────┴────────────┴───────────────┘              │  │
│  │                          │                                          │  │
│  │     ┌────────────────────▼─────────────────────────────┐           │  │
│  │     │        Agent Mesh Router (MAF Graph Workflow)     │           │  │
│  │     │  IntentAnalyzer → ContextInjector → AgentSelect  │           │  │
│  │     │  → CodeAgent/ChatAgent/EIA → OutputFormatter     │           │  │
│  │     └────────────────────┬─────────────────────────────┘           │  │
│  └──────────────────────────┼─────────────────────────────────────────┘  │
│                              │                                            │
│  ┌───────────────────────────▼──────────────────────────────────────┐   │
│  │                Middleware Pipeline (MAF AgentMiddleware)          │   │
│  │     PromptShield → InputClassifier → DNASafety → OutputReview    │   │
│  └──────────────────────────────────────────────────────────────────┘   │
│                                                                          │
│  ┌──────────────────────────────────────────────────────────────────┐   │
│  │                     LTAI Enhancement Layer                        │   │
│  │  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐              │   │
│  │  │  L1 Local    │ │  L2 Cloud    │ │  DNA Persona │              │   │
│  │  │  Brain       │ │  Brain       │ │  (8子系统)   │              │   │
│  │  │  (RWKV/ONNX) │ │  (DeepSeek)  │ │  + Safety    │              │   │
│  │  └──────┬───────┘ └──────┬───────┘ └──────┬───────┘              │   │
│  │         └────────────────┼────────────────┘                       │   │
│  │                          │                                         │   │
│  │  ┌───────────────────────▼────────────────────────────────────┐  │   │
│  │  │               Tool Ecosystem (126+ tools)                   │  │   │
│  │  │  General · Code · EIA · GIS · Web · Shell · Doc · Memory   │  │   │
│  │  └─────────────────────────────────────────────────────────────┘  │   │
│  └──────────────────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────────────────┘
```

---

## 项目结构 (10+1 Projects)

```
src/
├── LTAI.Core/              Foundation: config, models, DI, messaging, serialization
├── LTAI.Models/             Shared models (extracted from Core/Execution.Models)
├── LTAI.Agent/              MAF-native agent layer (replaces LTAI.MAF)
│   ├── Agents/              ChatAgent, CodeAgent, EIAAgent, ReasoningAgent
│   ├── Middleware/          PromptShield, InputClassifier, DNASafety, OutputReview
│   ├── Workflows/           MAF Graph Workflows for agent orchestration
│   └── Skills/              Agent skill definitions
├── LTAI.AI/                 Intelligence engine (simplified)
│   ├── Providers/           Multi-provider engine
│   ├── L1/                  Local brain (LlamaSharp, ONNX, Router)
│   ├── L2/                  Cloud brain routing
│   └── CellAI/              Hybrid intent classification
├── LTAI.DNA/                Persona & consciousness (30→8 subsystems)
│   ├── PersonaEngine.cs     Big Five traits + identity
│   ├── SafetyGuard.cs       Content safety middleware
│   ├── MemorySystem.cs      Emotional + episodic memory
│   ├── LifeEngine.cs        Biorhythm + presence
│   ├── EvolutionEngine.cs   Self-improvement loop
│   ├── ToolRepair.cs        Self-healing tools
│   ├── FeedbackLoop.cs      RLVR + evaluation
│   └── IdentityNarrative.cs Agent identity narrative
├── LTAI.Knowledge/          Merged Vector + Document + Memory
│   ├── Embedding/           ONNX + API embeddings
│   ├── Storage/             DocumentStore, BrainStore (SQLite+FTS5)
│   ├── RAG/                 AgenticRAG, hybrid search (semantic+BM25)
│   ├── Parsers/             JSON, XML, CSV, Markdown, etc.
│   └── Quality/             HallucinationGuard
├── LTAI.Tools/              Unified tool ecosystem on MAF AIFunction
│   ├── General/             filesystem, shell, http, math, text, data
│   ├── Code/                code analysis, git tools
│   ├── EIA/                 environmental assessment models
│   └── GIS/                 geo-spatial analysis tools
├── LTAI.Planning/           Merged Execution + Metrics
│   ├── Planner/             DiffusionPlanner, GTSM, ThompsonDelegator
│   ├── Quality/             Evaluation, GoldenQueryManager
│   └── Observability/       OpenTelemetry, ActivityFeed
├── LTAI.Infra/              Merged infrastructure layer
│   ├── Sandbox/             Code execution (process + Docker)
│   ├── Browser/             Playwright-based automation
│   ├── Network/             P2P, gRPC, distributed consensus
│   └── Multimedia/          OCR, Vision, Speech
├── LTAI.Economy/            Cost optimization (simplified)
│   ├── Evolution/           Tool chain evolution
│   ├── Budget/              Token budgeting
│   └── Profiling/           Hardware profiling
├── LTAI.Web/                REST API, SSE, OpenAI proxy
├── LTAI.Host/               Primary ASP.NET Core host
├── LTAI.TUI/                Terminal UI
├── LTAI.MCP/                MCP protocol host
├── LTAI.Desktop/            .NET MAUI
└── LTAI.WebApp/             Standalone web app
```

## 核心设计决策

### 1. Agent 层级 (MAF 原生)

```csharp
// 所有 Agent 继承 AIAgent，不包装额外层
public sealed class ChatAgent : AIAgent { ... }
public sealed class CodeAgent : AIAgent { ... }
public sealed class EIAAgent : AIAgent { ... }
public sealed class ReasoningAgent : AIAgent { ... }
```

### 2. Workflow (MAF Graph)

```csharp
var pipeline = new AgentWorkflow("pipeline")
    .AddAgent("intent", intentAgent)
    .AddAgent("chat", chatAgent)
    .AddAgent("code", codeAgent)
    .AddConditionalEdge("intent", ctx => GetIntent(ctx) switch
        { "code" => "code", _ => "chat" });
```

### 3. Middleware (MAF AgentMiddleware)

```csharp
agent.Use(new PromptShieldMiddleware())
     .Use(new InputClassifierMiddleware())
     .Use(new DNASafetyMiddleware())
     .Use(new OutputReviewMiddleware());
```

### 4. 工具（MAF AIFunction）

```csharp
[AIFunction("Read a file")]
public static async Task<string> ReadFile(
    [AIFunctionParameter("Path")] string path, ...) { }

agent.Tools.Add(AIFunctionFactory.Create(FileTools.ReadFile));
```

### 5. 声明式配置 (YAML)

```yaml
agents:
  - name: chat
    type: chat_agent
    model: deepseek-v4-pro
    instructions: "You are Little Tree..."
    middleware: [prompt_shield, dna_safety]
    tools: [filesystem, shell, http]
```

### 6. Provider 简化

```csharp
services.AddLTAIProviders(o => o
    .AddDeepSeek("deepseek-v4-pro", priority: 1)
    .AddOllama("qwen3.5:4b", priority: 3)
    .WithDegradationChain("deepseek-v4-pro", "deepseek-v4-flash")
    .WithL1L2Router(threshold: 0.7));
```

---

## 对比：v5.5 vs v6.0

| 指标 | v5.5 | v6.0 | 变化 |
|------|------|------|------|
| 源码项目 | 22 | 10+1 | -55% |
| DNA 子系统 | 30+ | 8 | -73% |
| Governor 文件 | 75 | 0 (MAF替代) | -100% |
| 工具注册系统 | 3套 | 1套 (AIFunction) | 统一 |
| Host 入口 | 5独立 | 1 Builder + 5 Profile | 统一 |
| LivingTreeSystem | 566行 | <200行 | -65% |
| 自定义 Workflow | 160行 | 10行(MAF Graph) | -94% |
| MAF 兼容性 | 半封装 | 100%原生 | 质变 |

---

*架构设计: 2026-05-22*
