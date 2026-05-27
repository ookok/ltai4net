# LTAI — Agent OS V1.0

6-layer Agent OS framework for .NET 10. Microkernel architecture with capability-based security, Pareto-optimal routing, autonomous evolution, and full-stack safety auditing.

## Quick Start

```bash
# Download CLI (single binary)
curl -L https://github.com/ookok/ltai4net/releases/latest/download/ltai-win-x64.exe -o ltai.exe

# First-run setup
./ltai init
./ltai install
./ltai up          # starts TUI by default
```

Or build from source:
```bash
git clone https://github.com/ookok/ltai4net
cd ltai4net
dotnet build
dotnet run --project src/LTAI.Cli -- init
dotnet run --project src/LTAI.Cli -- up
```

## Architecture (6-layer Agent OS)

```
L5  IAgent Apps     — CodeAgent, ChatAgent, EIAAgent, ReasoningAgent
L4  Evolution       — GenePool (GA), ArchitectLoop (LLM), SemanticDiffAgent
L3  Cognitive       — ParetoRouter (Q/S/C 3D), RecursiveCausalAudit, MCTS
L2  Runtime         — CoordinationScheduler, WorktreeManager, EvolutionLoop
L1  I/O Layer       — SkillSystem, MemoryGraph, MultimodalOrchestrator
L0  MicroKernel     — 11 primitives + CapToken + quotas + sandbox + audit
```

**Layer contract**: upper calls lower, lower never knows upper, peers communicate via `CoordinationScheduler` events, safety spans all layers.

## 8 Core Capabilities

| Capability | Rating | Key Components |
|-----------|:------:|---------------|
| **Perception** | 7.5 | HybridIntentRouter, MultimodalOrchestrator, PromptShield, ContextMoE |
| **Planning** | 7.0 | HTNPlanner, UniversalOrchestrator(5 modes), TaskQueue DAG |
| **Tools** | 9.0 | SkillRegistry(40+), ToolService(120+), MarkdownToolExecutor |
| **Memory** | 8.5 | ContextMoE(5-tier), KnowledgeBase, MemoryGraph, KnowledgeGraph |
| **Decision** | 8.5 | ParetoRouter(3D), BootstrapTeacher, MCTS Reasoning, CausalAudit |
| **Feedback** | 8.3 | AgenticLoop, BackpressurePipeline, DebugLoop, PartStreamStore |
| **Evolution** | 7.5 | GenePool(GA), SimulatedAnnealer, FederatedLearning |
| **Safety** | 9.2 | 3-layer cmd blocking, PolicyAsCode(16 rules), CapToken, HITL |

## CLI Commands

| Command | Description |
|---------|-------------|
| `ltai init` | Interactive setup: paths, channels, API keys, sandbox |
| `ltai install` | Download L0–L5 core runtime |
| `ltai setup` | Configure L0/L1/L2 models and providers |
| `ltai add <tui\|desktop\|webapi\|mcp\|webapp>` | Install component |
| `ltai remove <component>` | Uninstall component |
| `ltai up [component]` | Start component (default: tui) |
| `ltai down` | Stop all components |
| `ltai ps` | List component status |
| `ltai update [cli\|core\|all]` | Self-update |
| `ltai env` | List all environment variables (LTAI + provider API keys) |
| `ltai env get <KEY>` | Get a specific env var with masked secrets |
| `ltai env set <KEY> <VALUE>` | Persist env var to config or user environment |
| `ltai debug --query "…"` | Live pipeline trace with ParetoRouter metrics |
| `ltai debug --batch` | Run layered test suite from `docs/testprompts.txt` |
| `ltai debug --count N` | Generate N heuristic test cases via pipeline |

## Environment Variables

### LTAI Core (4 variables, persisted in `~/.ltai/config.json`)

| Variable | Purpose | Set via |
|----------|---------|---------|
| `LTAI_HOME` | Installation root directory | `ltai init` / `ltai env set` |
| `LTAI_WORKSPACE` | Project workspace root (40+ consumers) | `ltai init` / `ltai env set` |
| `LTAI_L1_API_KEY` | Fast model (L1) API key | `ltai init` / `ltai env set` |
| `LTAI_L2_API_KEY` | Deep model (L2) API key | `ltai init` / `ltai env set` |

### Provider API Keys (27 providers, resolved by `ResolveApiKey`)

| Provider | Environment Variable | Provider | Environment Variable |
|----------|---------------------|----------|---------------------|
| DeepSeek | `DEEPSEEK_API_KEY` | OpenAI | `OPENAI_API_KEY` |
| Anthropic | `ANTHROPIC_API_KEY` | Gemini | `GEMINI_API_KEY` |
| SiliconFlow | `SILICONFLOW_API_KEY` | Aliyun (DashScope) | `DASHSCOPE_API_KEY` |
| Zhipu | `ZHIPU_API_KEY` | Hunyuan | `HUNYUAN_API_KEY` |
| Baidu | `BAIDU_API_KEY` | iFlytek Spark | `SPARK_API_KEY` |
| Mofang | `MOFANG_API_KEY` | NVIDIA | `NVIDIA_API_KEY` |
| Bailing | `BAILING_API_KEY` | StepFun | `STEPFUN_API_KEY` |
| InternLM | `INTERNLM_API_KEY` | SenseTime | `SENSETIME_API_KEY` |
| ModelScope | `MODELSCOPE_API_KEY` | OpenRouter | `OPENROUTER_API_KEY` |
| Xiaomi | `XIAOMI_API_KEY` | LongCat | `LONGCAT_API_KEY` |
| DMXAPI | `DMXAPI_API_KEY` | Volcengine | `VOLCENGINE_API_KEY` |
| Moonshot | `MOONSHOT_API_KEY` | MiniMax | `MINIMAX_API_KEY` |
| Groq | `GROQ_API_KEY` | Kiro | `KIRO_API_KEY` |
| OpenCode | `OPENCODE_API_KEY` | Unknown | `{NAME}_API_KEY` |

**Local providers** (no API key needed): Ollama, LMStudio, vLLM, LlamaCpp, OpenWebUI.

### Managing env vars

```bash
ltai env                          # table of all 31 vars with masked secrets
ltai env get DEEPSEEK_API_KEY     # sk-a****bc12
ltai env set LTAI_WORKSPACE /home/project
ltai env set OPENAI_API_KEY sk-xxx  # writes to user environment
```

## Test Suite

### Full-Chain Debug Query Tests (42 tests, all passing)

Organized by layer with one test per CLI debug query prompt. Each test validates audit log entries and system behavior.

| File | Tests | Coverage |
|------|:-----:|----------|
| `DebugQueryL0L1Tests.cs` | 14 | L0 security (sandbox/path/network/command/quota) + L1 perception (skills/memory/multimodal) |
| `DebugQueryL2L3Tests.cs` | 11 | L2 runtime (worktree/backpressure/heartbeat) + L3 cognitive (ParetoRouter/MCTS/SLA) |
| `DebugQueryL4L5Tests.cs` | 17 | L4 evolution (GenePool/Annealer/ArchitectLoop/HITL) + L5 agents (Code/Chat/Reasoning/Bootstrap) + CHAOS |

```bash
dotnet test --filter "FullyQualifiedName~DebugQuery"  # 42 passed, 0 failed
```

## Module Map (17 projects)

| Project | Role |
|---------|------|
| LTAI.Core | MicroKernel, ParetoRouter, config, messaging, session, EvolutionLoop |
| LTAI.Agent | Agents, AgenticLoop, MAF, TreeLLM, Workflows, SkillSystem |
| LTAI.AI | LivingTreeSystem, ONNX engines, Governors, Paper implementations |
| LTAI.DNA | Consciousness, Safety, LifeEngine, PolicyAsCode, SelfEvolution |
| LTAI.Knowledge | Vector+Document+Memory (RAG, BM25, ONNX embeddings) |
| LTAI.Tools | 120+ .md tools, BuildPipeline, TestHarness, Capability registry |
| LTAI.Planning | HTN planning, execution, metrics, SelfHealer |
| LTAI.Infra | Sandbox, Browser, Network, Multimodal (OCR/STT/Vision) |
| LTAI.Economy | Cost optimization, budget tracking, metabolic model |
| LTAI.Web | REST API (~80 routes), SSE endpoints, GitHub OAuth |
| LTAI.Host | ASP.NET Core host entry point |
| LTAI.TUI | Terminal UI (Spectre.Console, 10-panel dashboard) |
| LTAI.MCP | MCP protocol client + server |
| LTAI.Desktop | .NET MAUI desktop app |
| LTAI.WebApp | Blazor Server web UI |
| LTAI.Cli | CLI Bootstrapper (installer/launcher/env/debug) |
| LTAI.Models | Shared POCOs, enums, execution models |

## Key Safety Mechanisms

| Layer | Mechanism |
|------|-----------|
| L0 | CapToken (HMAC capability security), path/network/command sandbox, resource quotas (10MB/file, 100MB total, 50MB read, 4 concurrent processes) |
| L1 | 3-layer command blocking, PromptShield (10 injection + 5 output patterns), UnifiedSafetyGate with cumulative risk tracking |
| L4 | SemanticDiffAgent (18 danger patterns), CounterfactualGate (shadow routing), ArchitectLoop risk threshold (>0.7 triggers HITL) |
| All | PolicyAsCode (16 rules, hot-reload YAML), full audit trail (`KernelAuditEntry` FIFO), HITL for high-risk proposals |

## Observability

| Interface | What It Exposes |
|-----------|----------------|
| `CPSProcessingService.ExplainLastDecision()` | Human-readable ParetoRouter decision trace |
| `CPSProcessingService.GetPerformanceStats()` | Latency / tokens / route distribution |
| `CoordinationScheduler.GetHealthReport()` | Event bus queue depth, rules triggered |
| `IMicroKernel.GetAuditTrail(limit)` | Full kernel operation audit (P50/P99) |
| `IMicroKernel.GetVitalSigns()` | CPU/memory/disk health vitals |
| `GET /api/v7/status` | JSON dashboard (CPS, scheduler, pareto, kernel) |
| Pipeline Dashboard | TUI 10 panels, Desktop, WebApp |

## Build & Development

```bash
dotnet build                          # 17 projects
dotnet test                           # full xUnit suite (620 tests)
dotnet test --filter "~DebugQuery"    # new full-chain tests only
docker-compose up                     # Docker deployment
```

### Project conventions

- **C# / .NET 10.0** with Nullable + ImplicitUsings on every project
- `<RootNamespace>` set explicitly in every `.csproj`
- Library output → `dist/lib/`; exe projects → `dist/<Name>/` (gated by `IsExeProject`)
- Test naming: `{Category}_{Number}_{Description}` — e.g. `L0_SEC_01_PathTraversal_BlockedBySandbox`
- Four-pillar `.md` system: `tools/`, `prompts/`, `skills/`, `memory/` share `.md` loader

## Documentation

- [Full Architecture](docs/architecture.md) — 6-layer design, 8-thread capability matrix, onboarding guide
- [REASONIX.md](REASONIX.md) — Auto-pinned session context for Reasonix Code
- [Test Prompts](docs/testprompts.txt) — 40+ layered test prompts (L0–L5 + CHAOS)
- [Test Expected Results](docs/test_expected.csv) — Audit log keyword patterns per test

## License

MIT
