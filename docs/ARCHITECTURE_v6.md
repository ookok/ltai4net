# LTAI v6.0 — ONNX-Native Agent Mesh Architecture

基于 Microsoft Agent Framework (MAF) 1.6.2 全面重构，以 ONNX Runtime 为核心本地推理引擎。

---

## 架构全景

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         LTAI Host Layer (5入口)                           │
│ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌────────────────┐  │
│ │ Web API  │ │   TUI    │ │   MCP    │ │ Desktop  │ │    WebApp      │  │
│ │ (8080)   │ │(Spectre) │ │(Protocol)│ │ (MAUI)   │ │ (Standalone)   │  │
│ └────┬─────┘ └────┬─────┘ └────┬─────┘ └────┬─────┘ └───────┬────────┘  │
│      └───────────┴──────────────┴──────────────┴────────────────┘         │
│                                    │                                      │
├────────────────────────────────────┼──────────────────────────────────────┤
│                        LivingTreeSystem (462行, 18参, 5 Governor)         │
│  ChatAsync → DNA安全 → L1/L2路由 → Provider选择 → LLM调用 → 输出审查     │
│  自动集成: BAVT预算 + ERL学习 + ElasticMemory + StructuredReflection     │
│           + CoEchoDetector(回音检测) + OTESelector(Provider裁剪)         │
├────────────────────────────────────┼──────────────────────────────────────┤
│                                                                          │
│  ┌───────────┐  ┌──────────────┐  ┌──────────────┐                       │
│  │  Agent层  │  │ Middleware层  │  │  Tool Ecosystem                    │
│  │Chat/Code  │  │PromptShield  │  │  70+ tools: General·Code·GIS·EIA·   │
│  │EIA/Reason │  │InputClassify │  │  Web·Shell·Doc·Search·Integration   │
│  │           │  │DNASafety     │  │                                      │
│  │AgentMesh  │  │OutputReview  │  │  Agent.Tools(AIFunctionFactory)     │
│  │Workflow   │  │+ConfGate     │  │                                      │
│  └───────────┘  └──────────────┘  └──────────────┘                       │
│                                                                          │
│  ┌───────────────────────────────────────────────────────────────────┐  │
│  │                    L0 → L1 → L2 三层架构                           │  │
│  │                                                                     │  │
│  │  L0 (Embed)—ONNX──────────────────────────────│                    │  │
│  │  ├─ Jina-v5-Omni (768-dim, 多模态)            │ 向量检索 + RAG     │  │
│  │  ├─ BGE-Large-ZH / BGE-M3                    │                    │  │
│  │  └─ API fallback: SiliconFlow                 │                    │  │
│  │                                                                     │  │
│  │  L1 (Fast)—ONNX Native Edge──────────────────│                    │  │
│  │  ├─ Qwen2.5 1.5B ONNX (中文主力, 4GB)        │ 本地推理            │  │
│  │  ├─ Phi-3.5-Mini ONNX (推理增强, 8GB)        │ 意图识别+工具调用   │  │
│  │  └─ SmolLM2 360M ONNX (极致边缘, 1GB)        │ OnnxParallelEngine │  │
│  │                                                                     │  │
│  │  OnnxModelPipeline: intent→domain→tool→param  4级本地管线          │  │
│  │                                                                     │  │
│  │  L2 (Deep)—Cloud API─────────────────────────│                    │  │
│  │  ├─ DeepSeek-v4-Pro / Flash                   │ 复杂推理            │  │
│  │  ├─ Qwen-Max                                   │ 中文深度推理        │  │
│  │  ├─ OpenAI GPT-4o                              │ 代码分析            │  │
│  │  └─ MultiProvider + CoEchoDetect + FanOutRace  │ 多Provider并行      │  │
│  └───────────────────────────────────────────────────────────────────┘  │
│                                                                          │
│  ┌───────────────────────────────────────────────────────────────────┐  │
│  │                    ONNX/ML.NET Training Loop                        │  │
│  │  SynapticTrainer → ONNX权重导出 → InferenceSession (无ML.NET开销)  │  │
│  │  IidChangePointDetector (异常检测) + ToolRecommender (协同过滤)    │  │
│  │  OnnxInt8Quantizer (FP32→INT8, 内存-75%)                          │  │
│  └───────────────────────────────────────────────────────────────────┘  │
│                                                                          │
│  ┌───────────────────────────────────────────────────────────────────┐  │
│  │                    Knowledge & DNA Layer                            │  │
│  │  Knowledge(71文件): AgenticRAG·BM25·VectorStore·Doc·Memory        │  │
│  │  DNA(23文件): Consciousness·Safety·Life·Evolution·FeedbackLoop   │  │
│  │  OrthogonalRouter: KnowledgeGraph ⊥ ContextGovernor (HSD正交分解)  │  │
│  └───────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────┘
```

---

## 项目结构 (17 Projects)

```
src/
├── LTAI.Core/         (83)  Foundation: config, DI, messaging, LTAIHostBuilder
├── LTAI.Models/       (5)   Shared models + ExecutionModels
├── LTAI.Agent/        (116) MAF-native agents + middleware + workflows
│   ├── Agents/              ChatAgent, CodeAgent, EIAAgent, ReasoningAgent
│   ├── Middleware/          PromptShield(ConfidenceGate), InputClassifier,
│   │                          DNASafety, OutputReview
│   ├── Workflows/           AgentMeshWorkflow
│   ├── MAF/                 Legacy MAF (DevUI, Evolution, Tools, Governance)
│   └── TreeLLM/             Legacy TreeLLM (Session, Prompting, Routing)
├── LTAI.AI/           (85)  Intelligence engine — LivingTreeSystem + ONNX/ML
│   ├── Governors/           ATLAS FunctionalTokenRouter, TokenGateDecider
│   │                        AtlasThinkingPipeline, LatentAnchoredGRPO
│   │                        OrthogonalRouter (HSD), HeuristicLearning
│   │                        BAVTRouter, ERLLoop, ElasticMemory
│   │                        StructuredReflection, OnnxParallelEngine
│   │                        OnnxModelPipeline, IidChangePointDetector
│   │                        ToolRecommender, OnnxInt8Quantizer
│   │                        SynapticTrainer(ONNX)+SynapticInference
│   └── Providers/           MultiProviderChatClient, ProviderFanOutRace
│                            RescueParsingChatClient, MPMoEProvider
├── LTAI.DNA/          (23)  Persona & consciousness (32→23 files simplified)
├── LTAI.Knowledge/    (71)  Merged Vector+Document+Memory
│   ├── Core/                AgenticRAG, BM25, KnowledgeGraph, Reranker
│   ├── Vector/Embedding/    ONNX, Jina, API embeddings
│   └── Memory/              Emotional, Persona, UserModel
├── LTAI.Tools/        (74)  Unified tool ecosystem (from Capability)
│   ├── General/             FileSystem, Http, Math, Shell tools
│   ├── CodeEngine/          Roslyn, TreeSitter parsers
│   ├── GIS/                 GIS models, MapServices
│   └── Integration/         Weather, Translate, SMS, WebSearch
├── LTAI.Planning/     (38)  Merged Execution+Metrics
├── LTAI.Infra/        (41)  Merged Sandbox+Browser+Network+Multimodal
├── LTAI.Economy/      (15)  Cost optimization + model pricing
├── LTAI.Web/          (17)  REST API, SSE endpoints
├── LTAI.Host/         (5)   Primary ASP.NET Core host
├── LTAI.TUI/          (16)  Terminal UI (Spectre.Console)
├── LTAI.MCP/          (9)   MCP protocol server
├── LTAI.Desktop/      (19)  .NET MAUI desktop app
├── LTAI.WebApp/       (6)   Blazor Server web app
└── LTAI.Cli/          (12)  CLI tools (model manage, debug, improve)
```

---

## 论文集成清单

| 论文 | 位置 | 效果 |
|------|------|------|
| **ATLAS** (2605.15198) | `AtlasFunctionalToken.cs` | 功能令牌路由替代规则路由 |
| **HSD** (2605.13834) | `AtlasFunctionalToken.cs:OrthogonalRouter` | KnowledgeGraph ⊥ ContextGovernor |
| **MP-MoE** (ICML 2026) | `MPMoEProvider.cs` | Provider回音检测+OT裁剪 |
| **ERL** (2602.13949) | `PaperImplementations.cs:ERLLoop` | 尝试→反思→修复→巩固循环 |
| **BAVT** (2603.12634) | `PaperImplementations.cs:BAVTRouter` | 预算退火节点选择 |
| **AutoAgent** (2603.09716) | `PaperImplementations.cs:ElasticMemory` | 原始→压缩→情节三层存储 |
| **Confidence** (2601.07264) | `PaperImplementations.cs:ConfidenceCalibrator` | 证据工具-25%惩罚 |
| **StructuredReflection** (2509.18847) | `PaperImplementations.cs:StructuredReflectionEngine` | 诊断→修复→重试→放弃 |
| **ToolMind** (2511.15718) | `PaperImplementations.cs:ToolRecommender` | 协同过滤工具推荐 |
| **Dr. Zero** (2601.07055) | `PaperImplementations.cs:HRPO` | 组相对策略优化 |

---

## 核心设计

### L0-L1-L2 自适应路由

```csharp
// 查询进入 → FunctionalTokenRouter 分类
var route = await _router.RouteAsync(query);
// AnswerDirect → L1 ONNX本地回答
// ThinkHard → L1 OnnxParallelEngine 多模型并行
// EscalateL2 → Cloud API
```

### ONNX 训练闭环

```csharp
// 训练: ML.NET → ONNX权重导出
var result = synapticTrainer.TrainIntentClassifier(samples);
// → intent_classifier_xxx.onnx (InferenceSession可直接加载)

// 推理: ONNX原生 (无ML.NET开销)
synapticInference.LoadOnnxModel(result.OnnxPath);
var pred = synapticInference.Predict(query); // ModelType = "onnx"
```

### Jina v5 嵌入一键启用

```csharp
// config驱动 — appsettings.json 改一行
// "l0": {"model": "jina-embeddings-v5-omni-small"}
// ↓ AddLTAIVectorAuto 自动识别 jina- 前缀, 自动下载HF模型
```

---

## 对比：v5.5 vs v6.0 vs v6.1 (Current)

| 指标 | v5.5 | v6.0 | v6.1 |
|------|------|------|------|
| 源码项目 | 29 | 10+1 | **17** |
| SDK | MAF 1.6.1 | MAF 1.6.2 | **MAF 1.6.2** |
| ONNX L1模型 | 0 | 0 | **5** (SmolLM/Qwen/Phi) |
| L0嵌入 | API only | API+BGE | **API+BGE+Jina v5** |
| 论文落地 | 0 | 2 | **10** |
| 死DI/文件清理 | — | — | **60+DI, 73文件** |
| 命名空间 | 100+ | 80 | **~50 (扁平化6层)** |
| LivingTreeSystem | 566行,22参 | 462行,18参 | **462行,18参** |
| DNA文件 | 32 | 31 | **23** |
| Provider路由 | 单路LLM | 多Provider | **多Provider+CoEchoDetect** |

---

*最后更新: 2026-05-23*
