# LTAI (LivingTree AI)

A biologically-inspired, multilayered AI agent framework for .NET 10. Built on a "Living Tree" governance architecture with 10+ specialized cognitive layers, multi-model routing, autonomous self-evolution, and comprehensive tool integration.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                    HOSTING / ENTRY POINTS                        │
│  LTAI.Host (Web API) · LTAI.TUI (Console) · LTAI.MCP (MCP)     │
│  LTAI.Desktop (MAUI) · LTAI.WebApp                              │
└────────────────────────────┬────────────────────────────────────┘
                             │
┌────────────────────────────▼────────────────────────────────────┐
│                     AGENT FRAMEWORK                              │
│  LTAI.MAF — LTAIAgent (IChatClient) · A2AHost · ChatHistory     │
│  LTAI.Web — REST / SSE / OpenAI Proxy / Auth / Workspace        │
└────────────────────────────┬────────────────────────────────────┘
                             │
┌────────────────────────────▼────────────────────────────────────┐
│                     AI INTELLIGENCE                              │
│  LTAI.AI — ProviderEngine · LivingTreeSystem · 10 Governors      │
│  LTAI.DNA — Consciousness · Self-Evolution · Safety · Identity  │
│  LTAI.TreeLLM — Prompting · MCTS · Routing · Consensus          │
│  LTAI.Economy — Cost · Evolution · Hardware Profile · Budget    │
└────────────────────────────┬────────────────────────────────────┘
                             │
┌────────────────────────────▼────────────────────────────────────┐
│                     DOMAIN CAPABILITIES                          │
│  LTAI.Vector — Embeddings · KnowledgeBase · RAG · BrainStore    │
│  LTAI.Capability — Tools · Reasoning · GIS · Search · Skills    │
│  LTAI.Execution — Planning · Quality · Session · Sandbox        │
│  LTAI.Document · LTAI.Browser · LTAI.Memory · LTAI.Multimodal    │
│  LTAI.Network — P2P · Swarm · Consensus · MassTransit           │
│  LTAI.Metrics — Evaluation · Monitoring · Auditing              │
└────────────────────────────┬────────────────────────────────────┘
                             │
┌────────────────────────────▼────────────────────────────────────┐
│                     CORE INFRASTRUCTURE                          │
│  LTAI.Core — Configuration · Models · Messaging · System        │
└─────────────────────────────────────────────────────────────────┘
```

## Module Overview

### LTAI.Core — Foundation
Configuration (`LTAIOptions`), models (`EIAModels`, `Handshake`), system services (`CognitiveMesh`, `TextClassifier`, `SovereigntyGapDetector`, `SocialLoadModel`), acceleration (`HotPathObjectPool`), tool registry (`AIToolRegistry`), and messaging (`EventBusV2`).

### LTAI.AI — Intelligence Engine
- **LivingTreeSystem**: Master orchestrator routing queries through 10 specialized governors
- **Governors**: Input → Context → Routing → LLM → Output → Self, plus Capability, Storage, Communication, Task, Evolution, and SystemGuardian
- **ProviderEngine**: Multi-provider LLM backend (DeepSeek, OpenAI, Anthropic, Gemini, Ollama) with streaming, budget tracking, and fan-out racing (`ProviderFanOutRace`)
- **CapabilityBus**: Unified capability dispatch (tools, skills, VFS) with adapter pattern

### LTAI.DNA — Bio-Inspired Consciousness
30+ subsystems: DualConsciousness, SelfEvolution, SafetyCoordinator, ImmuneSystem, HormoneNetwork, BiorhythmEngine, Personality (Big Five), IdentityNarrative, GodelianSelf, MentalTimeTravel, SheshaHeads (multi-headed attention).

### LTAI.TreeLLM — Reasoning & Prompting
- **PromptBuilder**: Structured prompt construction with knowledge fusion
- **MctsAgentReasoner**: Monte Carlo Tree Search reasoning with UCB scoring
- **ParallelReasoningGraph**: DAG-based multi-branch parallel reasoning
- **SelfDistillPipeline**: Template discovery from reasoning traces
- **SelfRefinementLoop**: Iterative answer refinement with hallucination guard
- **ContinuousLearningLoop**: Closed-loop feedback for role/prompt optimization
- **Routing**: BudgetRouter, LatencyOracle, AutoTunerBridge (Thompson sampling)
- **Consensus**: MultiModelConsensus, ThreeModelIntelligence

### LTAI.Economy — Cost & Evolution
- **EvolutionEngine**: Genetic algorithm for prompt/code evolution with tournament selection
- **EconomicEngine**: ROI modeling with task-type valuation
- **TieredEvaluator**: Multi-pass (correctness → security → profiling)
- **HardwareProfiler**: Latency/compute/memory profiling
- **PromptPool**: Weighted prompt template sampling
- **ThermoBudget**: Thermodynamic budget with entropy/temperature/entropy/KL constraints
- **MetabolismEngine**: Organ-level resource allocation (12 organs)

### LTAI.Vector — Knowledge & Retrieval
- **KnowledgeBase**: Hybrid search (semantic + keyword via Reciprocal Rank Fusion)
- **DocumentStore**: SQLite + FTS5 with chunked storage and vector indexing
- **UnifiedBrainStore**: All-in-one store (SQLite + BM25 + vectors + compiled truth)
- **KnowledgeCompiler**: Iterative domain curation with convergence tracking
- **MarkdownKnowledgeGraph**: Markdown-to-graph parser with link validation
- **AgenticRAG** · **HallucinationGuard** · **MemoryPoisoningDefense**
- **EmbeddingQuantizer**: Float → byte quantization with min-max normalization

### LTAI.Capability — Domain Tools (80+ tools)
- **EIA Models**: Gaussian plume, AERMOD, CALPUFF, GRAL, noise ISO 9613, Streeter-Phelps, carbon/ecological risk (21 environmental tools)
- **GIS**: Geocode, buffer, spatial search, coordinate transform
- **Reasoning**: Math, Formal Logic, Dialectical, Attribution
- **Knowledge**: KernelMemory RAG, vector search, code graph
- **Code**: MultiLangCodeAnalyzer, CodeReviewEngine, sandbox exec
- **Integration**: Email, SMS, Telegram, WeChat Work, Weather, Translation, Image Search
- **Pipeline**: LLM-driven data processing (Extract → Map → Filter → Reduce)
- **Self-Improvement**: SelfModifier, SelfDiscovery, SelfDocumenter

### LTAI.MAF.Tools — General Tools (43 tools)
- **filesystem**: read, write, list, delete, exists, search
- **shell**: command exec (dangerous-command safe), environment info
- **http**: GET, POST, download (base64), status check
- **math**: expression eval, base convert, unit convert, random, statistics
- **text**: count, hash (MD5/SHA1/256/384/512), base64, JSON format, case convert, regex replace/extract
- **data**: CSV parse, JSONPath query, format convert, pretty print, pluck
- **datetime**: now, from timestamp, date diff, date add
- **code**: quick stats, snippet generation, JSON→class definition
- **env**: system info, env var, processes, network/ping
- **web**: page fetch, metadata extract, DuckDuckGo search

> Full tool catalog: [TOOLS.md](./TOOLS.md) — 126+ tools across 20 categories

### LTAI.Execution — Planning & Quality
- **Planning**: DiffusionPlanner, GTSM, CostAware (token budget)
- **Quality**: ThompsonDelegator, FitnessLandscape (Pareto front), AutoSkillResolver
- **Session**: SessionManager, SideGit, TerminalCompressor

### LTAI.Metrics — Observability
`GoldenQueryManager`, `LayerIsolationEvaluator` (RAG pipeline diagnostics), `RetrievalMonitor`, `ActivityFeed` (500-entry pub-sub event bus), `LTAIMetricsCollector` (OpenTelemetry).

### LTAI.MAF — Multi-Agent Framework
`LTAIAgent` implements `IChatClient`, `A2AHost` (Agent-to-Agent protocol), `ChatHistoryStore` (file/blob/cosmos backends), `GovernorMiddleware`, DevUI, SignalR streaming.

### Supporting Modules
- **LTAI.Document**: 9 parsers (JSON, XML, CSV, Markdown, YAML/TOML, HTML, INI, Log, Text)
- **LTAI.Browser**: Playwright-based web automation
- **LTAI.Sandbox**: Process + Docker sandboxes for code execution
- **LTAI.Memory**: EmotionalMemory, PersonaMemory, UserModel, MemPO optimization
- **LTAI.Multimodal**: OCR (Tesseract), Vision, Speech (System.Speech)
- **LTAI.Network**: P2P, swarm coordination, MassTransit/RabbitMQ, distributed consciousness

## Tech Stack

| Area | Technology |
|------|-----------|
| Runtime | .NET 10.0 |
| Web | ASP.NET Core |
| AI | Microsoft.Extensions.AI 10.6, SemanticKernel 1.76 |
| RAG | Microsoft.KernelMemory 0.97 |
| Agents | Microsoft.Agents.AI 1.6 |
| Browser | Microsoft.Playwright 1.59 |
| TUI | Spectre.Console 0.55 |
| Observability | OpenTelemetry · Serilog · Jaeger |
| Messaging | MassTransit · RabbitMQ · Redis |
| Storage | SQLite · FTS5 · HNSW (in-memory vectors) |
| Container | Docker · Docker Compose |
| Desktop | .NET MAUI |

## Quick Start

### Prerequisites
- .NET 10.0 SDK
- Docker (optional, for sandbox + sidecar services)

### Configuration

Copy and edit `src/LTAI.Host/appsettings.json`:

```json
{
  "LTAI": {
    "data_directory": ".livingtree",
    "AI": {
      "default_provider": "deepseek",
      "providers": {
        "deepseek": { "api_key": "sk-...", "base_url": "https://api.deepseek.com/v1" }
      }
    }
  }
}
```

Set environment variables for API keys:
- `DEEPSEEK_API_KEY`
- `OPENAI_API_KEY`
- `ANTHROPIC_API_KEY`
- `SILICONFLOW_API_KEY`

### Run (Development)
```bash
cd src/LTAI.Host
dotnet run
```

### Run (Docker)
```bash
docker compose up -d
```

## Key Features

### Three-Tier Model Strategy
- **L0** (embedding): siliconflow / BAAI/bge-large-zh-v1.5
- **L1** (fast): deepseek / deepseek-v4-flash
- **L2** (deep reasoning): deepseek / deepseek-v4-pro
- Automatic degradation chain when budget is exceeded

### Living Tree Governance Pipeline
```
User Query → InputGovernor → ContextGovernor → RoutingGovernor
    → ProviderEngine (LLM) → OutputGovernor → SelfGovernor
    → Response
```
With DNA safety checks, reasoning enhancement, and self-review passes.

### Supported AI Providers (28+)
DeepSeek, OpenAI, Anthropic, Google Gemini, Alibaba Qwen, Zhipu GLM, Tencent Hunyuan, Baidu, iFlytek Spark, SiliconFlow, NVIDIA, Groq, Moonshot, MiniMax, Ollama, and more.

### Knowledge Pipeline
Documents → Parser → DocumentStore (chunking + FTS5 + vectors) → RAG pipeline → PromptBuilder → LLM → Self-Refinement → Continuous Learning

### Self-Evolution
DNA-driven evolution rules + economic evolution engine + prompt pool with genetic algorithm → continuous improvement of responses and tool usage.

## API Endpoints

| Prefix | Description |
|--------|-------------|
| `/api/maf/*` | Agent chat, A2A protocol, DevUI |
| `/api/dna/*` | Consciousness, personality, safety, identity (30+ endpoints) |
| `/api/capability/*` | Tools, skills, reasoning, search, GIS, code analysis |
| `/api/sandbox/*` | Code execution, templates |
| `/api/metrics/*` | Observability, evaluation, audit, activity |
| `/api/execution/*` | Planning, quality, session, checkpoint |
| `/api/multimodal/*` | Vision, OCR, speech |
| `/api/openai-proxy/*` | OpenAI-compatible chat proxy |
| `/api/sse/*` | Server-Sent Events streaming |
| `/api/health` | Health check |

## Project Structure

```
src/
├── LTAI.Core/           Foundation (config, models, messaging, system services)
├── LTAI.AI/             AI engine (governors, providers, LivingTreeSystem)
├── LTAI.Vector/         Embeddings, knowledge base, RAG, brain store
├── LTAI.TreeLLM/        Reasoning, prompting, routing, consensus
├── LTAI.Economy/        Cost, evolution, hardware profiler, budget
├── LTAI.DNA/            Consciousness, life, safety, meta-learning
├── LTAI.Capability/     Tools, reasoning, GIS, search, skills
├── LTAI.Execution/      Planning, quality, session management
├── LTAI.MAF/            MS Agent Framework integration, A2A
├── LTAI.Web/            REST API, SSE, proxy, auth
├── LTAI.Document/       Universal file parser
├── LTAI.Browser/        Playwright browser agent
├── LTAI.Sandbox/        Code execution sandbox
├── LTAI.Memory/         Emotional memory, persona, user models
├── LTAI.Multimodal/     OCR, vision, speech
├── LTAI.Network/        P2P, swarm, distributed consensus
├── LTAI.Metrics/        Evaluation, monitoring, auditing
├── LTAI.Host/           Primary ASP.NET Core host
├── LTAI.TUI/            Terminal UI (Spectre.Console)
├── LTAI.MCP/            MCP protocol host
├── LTAI.Desktop/        .NET MAUI cross-platform app
└── LTAI.WebApp/         Standalone web application
```

## License

Proprietary. All rights reserved.
