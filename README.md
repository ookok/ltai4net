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
L3  Cognitive       — ParetoRouter (Q/S/C 3D), RecursiveCausalAudit
L2  Runtime         — CoordinationScheduler, WorktreeManager, EvolutionLoop
L1  I/O Layer       — SkillSystem, MemoryGraph, MultimodalOrchestrator
L0  MicroKernel     — 11 primitives + CapToken + quotas + sandbox + audit
```

**Layer contract**: upper calls lower, lower never knows upper, peers communicate via events, safety spans all layers.

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
| `ltai install` | Download L0-L5 core runtime |
| `ltai setup` | Configure L0/L1/L2 models and providers |
| `ltai add <tui\|desktop\|webapi\|mcp\|webapp>` | Install component |
| `ltai remove <component>` | Uninstall component |
| `ltai up [component]` | Start component (default: tui) |
| `ltai down` | Stop all components |
| `ltai ps` | List component status |
| `ltai update [cli\|core\|all]` | Self-update |

## Module Map (17 projects)

| Project | Role |
|---------|------|
| LTAI.Core | MicroKernel, ParetoRouter, config, messaging, session |
| LTAI.Agent | Agents, AgenticLoop, MAF, TreeLLM, Workflows |
| LTAI.AI | LivingTreeSystem, ONNX engines, Governors, Paper impls |
| LTAI.DNA | Consciousness, Safety, LifeEngine, PolicyAsCode |
| LTAI.Knowledge | Vector+Document+Memory (RAG, BM25, ONNX embeddings) |
| LTAI.Tools | 120+ .md tools, BuildPipeline, TestHarness |
| LTAI.Planning | HTN planning, execution, metrics, SelfHealer |
| LTAI.Infra | Sandbox, Browser, Network, Multimodal |
| LTAI.Economy | Cost optimization, budget tracking |
| LTAI.Web | REST API (~80 routes), SSE endpoints |
| LTAI.Host | ASP.NET Core host entry point |
| LTAI.TUI | Terminal UI (Spectre.Console, 10-panel dashboard) |
| LTAI.MCP | MCP protocol client + server |
| LTAI.Desktop | .NET MAUI desktop app |
| LTAI.WebApp | Blazor Server web UI |
| LTAI.Cli | CLI Bootstrapper (installer/launcher) |
| LTAI.Models | Shared POCOs |

## Key Safety Mechanisms

| Layer | Mechanism |
|------|-----------|
| L0 | CapToken (HMAC capability security), path/network/command sandbox, resource quotas |
| L1 | 3-layer command blocking (8+32+6 patterns), PromptShield (10 injection + 5 output) |
| L4 | SemanticDiffAgent (18 danger patterns), CounterfactualGate (shadow routing) |
| All | PolicyAsCode (16 rules, hot-reload), full audit trail, HITL for high-risk proposals |

## Observability

- `CPSProcessingService.ExplainLastDecision()` — human-readable decision trace
- `CPSProcessingService.GetPerformanceStats()` — latency/tokens/routes
- `CoordinationScheduler.GetHealthReport()` — event bus status
- `IMicroKernel.GetAuditTrail()` / `GetVitalSigns()` — kernel audit + P50/P99
- `GET /api/v7/status` — JSON dashboard (cps/scheduler/pareto/kernel)
- Pipeline Dashboard (TUI 10 panels, Desktop, WebApp)

## Build

```bash
dotnet build           # 17 projects
dotnet test            # xUnit test suite
docker-compose up      # Docker deployment
```

## Documentation

- [Full Architecture](docs/architecture.md) — 6-layer design, 8-thread capability matrix, onboarding guide
- [REASONIX.md](REASONIX.md) — Auto-pinned session context for Reasonix Code

## License

MIT
