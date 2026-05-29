# REASONIX.md — LTAI (LivingTree AI)

## Stack
- **C# / .NET 10.0** — 17 src + 7 test projects in `LTAI.sln`
- **ASP.NET Core** — primary host (`LTAI.Host`), REST + SSE API (`LTAI.Web`)
- **Spectre.Console** — CLI (`LTAI.Cli`) and TUI (`LTAI.TUI`)
- **xUnit 2.9.3** — test framework
- **Serilog + OpenTelemetry** — structured logging, traces/metrics
- **ONNX Runtime + ML.NET** — local L0/L1 inference pipeline

## Architecture (6-layer Agent OS)
```
L5  IAgent apps    — CodeAgent, EIAAgent, ChatAgent, ReasoningAgent
L4  Evolution      — GenePool (mutation/crossover), ArchitectLoop, SemanticDiffAgent
L3  Cognitive      — ParetoRouter (multi-obj routing), RecursiveCausalAudit
L2  Runtime        — CoordinationScheduler, GitWorktreeManager, NicheIsolation
L1  I/O Layer      — SkillSystem ↔ MicroKernel bridge, MemoryGraph, KnowledgeBase
L0  MicroKernel    — 13 primitives (8 core + 5 evolution) + CapToken (HMAC-signed capability security)
```
Layer contract: upper calls lower (L4→L0), lower never knows upper, peer layers communicate via `CoordinationScheduler` events.

## Layout
| Dir | Purpose |
|-----|---------|
| `src/LTAI.Core/` | DI host builder, MicroKernel, config, messaging, session, resilience |
| `src/LTAI.Agent/` | Agents, middleware, MAF loop, Worktree workflows, ServiceCollectionExtensions (DI hub) |
| `src/LTAI.AI/` | LivingTreeSystem, ONNX engines, governors, paper implementations |
| `src/LTAI.DNA/` | Consciousness, safety, LifeEngine |
| `src/LTAI.Knowledge/` | Vector store, document parsing, AgenticRAG, BM25 |
| `src/LTAI.Tools/` | 12-domain .md tool definitions |
| `src/LTAI.Web/` / `Host/` | ASP.NET Web API + SSE endpoints, host entry point |
| `src/LTAI.Cli/` / `TUI/` / `Desktop/` / `WebApp/` | CLI, terminal UI, MAUI desktop, Blazor |
| `src/LTAI.Planning/` / `Infra/` / `Economy/` / `Models/` / `MCP/` | Planning, sandbox, cost, POCOs, MCP protocol |
| `tests/` | 7 test projects, one per domain |
| `config/` / `tools/` / `prompts/` / `skills/` / `memory/` / `rules/` | Runtime .md assets (four-pillar system) |

## Commands
```bash
dotnet build
dotnet run --project src/LTAI.Host
dotnet test
dotnet test tests/LTAI.Tests
docker-compose up
```

## Conventions
- **Nullable + ImplicitUsings** enabled on every project
- **`<RootNamespace>`** set explicitly in every .csproj
- **Library output** → `dist/lib/`; exe projects → `dist/<Name>/` (gated by `IsExeProject` in `Directory.Build.props`)
- **Test naming**: `{Category}_{Number}_{Description}` — e.g. `TC01_NormalPath_RoutesCodeRequestCorrectly` (xUnit `[Fact]`)
- **Four-pillar .md system**: tools, prompts, skills, and memory share `.md` loader; `MdToolBridge` wraps .md tools with C# fallback

## Watch out for
- `models/`, `dist/`, `*.meta.json` are gitignored — never committed
- `Directory.Build.props` requires `IsExeProject` / `IsTestProject` flags on exe/test .csproj, or output lands in `dist/lib/`
- `tools/` directory is the primary extensibility surface, not C# code
