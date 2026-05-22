# LTAI (LivingTree AI) v6.1

ONNX-native multilayered AI agent framework for .NET 10. Edge-first L0-L1-L2 architecture with autonomous self-evolution, 10 research paper integrations, and multi-provider intelligent routing.

## Quick Start

```bash
# Clone & run first-launch setup wizard
git clone https://github.com/ookok/ltai4net
cd ltai4net
dotnet run --project src/LTAI.Host

# Or use one-line Jina embeddings (config-driven, no code change):
# Edit appsettings.json: "l0": {"model": "jina-embeddings-v5-omni-small"}
```

## Architecture

```
Host Layer (Web API / TUI / MCP / Desktop / WebApp)
        │
LivingTreeSystem (462行, 18参数, 5 Governor)
  ├─ DNA Safety → Input Intent → L1/L2 Route → Provider Select → LLM Call
  ├─ 自动: BAVT(预算) + ERL(学习) + ElasticMemory + StructuredReflection
  └─ 自动: CoEchoDetect + OTESelector (Provider去重)
        │
  ┌─────┼─────┐
  ▼     ▼     ▼
 L0    L1    L2
Embed  Local Cloud
Jina   Qwen  DSv4
ONNX   ONNX  API
        │
  OnnxModelPipeline (intent→domain→tool→param 4级本地)
        │
Tool Ecosystem (70+ tools) + Knowledge (71 files)
        │
ONNX/ML.NET Training Loop (SynapticTrainer→InferenceSession)
```

## Module Map (17 projects, 635 .cs files)

| Project | Files | Role |
|---------|-------|------|
| LTAI.Core | 83 | Config, DI, messaging, LTAIHostBuilder |
| LTAI.Models | 5 | Shared models, ExecutionModels |
| LTAI.Agent | 116 | Agents(Chat/Code/EIA/Reasoning), Middleware, MAF, TreeLLM |
| LTAI.AI | 85 | LivingTreeSystem, Governors, ONNX engines, Paper implementations |
| LTAI.DNA | 23 | Consciousness, Safety, LifeEngine (32→23 simplified) |
| LTAI.Knowledge | 71 | Vector+Document+Memory (AgenticRAG, BM25, Jina, ONNX embeddings) |
| LTAI.Tools | 74 | General/Code/GIS/Integration tools |
| LTAI.Planning | 38 | Execution+Metrics |
| LTAI.Infra | 41 | Sandbox+Browser+Network+Multimodal |
| LTAI.Economy | 15 | Cost optimization, budget tracking |
| LTAI.Web | 17 | REST API, SSE endpoints |
| LTAI.Host | 5 | ASP.NET Core host |
| LTAI.TUI | 16 | Terminal UI (Spectre.Console) |
| LTAI.MCP | 9 | MCP protocol server |
| LTAI.Desktop | 19 | .NET MAUI |
| LTAI.WebApp | 6 | Blazor Server |
| LTAI.Cli | 12 | CLI tools |

## Key Features

### L0-L1-L2 ONNX-Native Routing
- **L0**: Jina-v5-Omni (768-dim multimodal), BGE-Large/Small/M3 ONNX embeddings
- **L1**: Qwen2.5-1.5B, Phi-3.5-Mini, SmolLM2-360M ONNX — edge-native local inference
- **OnnxParallelEngine**: intent + entity + sentiment 3-model parallel inference
- **OnnxModelPipeline**: 4-stage intent→domain→tool→param local pipeline (<10% L2 calls)
- **L2**: DeepSeek-v4, Qwen-Max, GPT-4o cloud API with MultiProvider routing

### Self-Evolution Training Loop
```
Training samples → SynapticTrainer(ML.NET) → ONNX weights export
    → SynapticInference(InferenceSession, no ML.NET overhead)
    → IidChangePointDetector(anomaly detection)
    → ToolRecommender(collaborative filtering)
```

### Research Papers Integrated (10 papers)
| Paper | Component |
|-------|-----------|
| ATLAS (2605.15198) | FunctionalTokenRouter + LatentAnchoredGRPO |
| HSD (2605.13834) | OrthogonalRouter (KnowledgeGraph ⊥ ContextGovernor) |
| MP-MoE (ICML 2026) | CoEchoDetector + OTESelector (Provider pruning) |
| ERL (2602.13949) | ERLLoop (experience→reflection→consolidation) |
| BAVT (2603.12634) | BAVTRouter (budget-annealed routing) |
| AutoAgent (2603.09716) | ElasticMemoryOrchestrator (3-layer memory) |
| Learning Beyond Gradients | HeuristicRegistry + HLFeedbackCycle |
| Confidence Gate | ConfidenceCalibrator (evidence tool -25%) |
| Structured Reflection | StructuredReflectionEngine (diagnose→repair→retry) |
| Dr. Zero (2601.07055) | ToolRecommender (collaborative filtering) |

### Configuration-Driven
- `appsettings.json`: `"l0": {"model": "jina-embeddings-v5-omni-small"}` → auto-detect, auto-download
- First-run `InteractiveSetupWizard`: step-by-step model/API selection with HF downloads
- All 5 hosts use `AddLTAIVectorAuto()` → auto-detect L0 model from config

## Build

```bash
dotnet build LTAI.sln        # 16/16 projects (Desktop needs Android workload)
dotnet run --project src/LTAI.Host
```

## SDK Versions

- .NET 10
- Microsoft.Agents.AI 1.6.2
- Microsoft.Extensions.AI 10.6.0
- Microsoft.ML 4.0.0 + OnnxRuntime 1.22.0
- LLamaSharp 0.26.0 (GGUF local models)

## License

MIT
