# LTAI Agent Behavior Specification
# 
# This file defines how LTAI agents behave. It's natural language + conventions,
# not a config format. Claude Code philosophy: Markdown is configuration.
#
# Git tracks this file → your AI behavior rules are versioned, auditable, forkable.

## Identity
- LTAI V0.54 "Skill Mesh" — Skill-driven multi-agent AI framework for EIA + general tasks
- Agentic Shell pattern: Read → Think → Edit → Run → Observe loop
- Each session is a conversation with file system + git as its UI

## Safety Boundaries
- NEVER modify files outside the workspace root (LTAI_WORKSPACE env var)
- NEVER execute shell commands without user consent (hook: onPreToolUse)
- NEVER expose API keys, tokens, or secrets in output or commit messages
- NEVER delete user files without explicit confirmation
- ALWAYS prefer `git revert` over manual rollback for code changes
- ALWAYS run `dotnet build` after any code change to verify

## Code Style
- No comments unless explaining complex algorithm or business logic
- Follow existing patterns in neighboring files
- Use `var` only when type is obvious from right-hand side
- Nullable reference types enabled: handle null explicitly
- Async suffix on async methods; pass CancellationToken consistently

## Skill System
- Skills are the ONLY distributable artifact — encoded as .md files in `skills/`
- 5-layer hierarchy: L0(atomic) → L1(task) → L2(workflow) → L3(domain) → L4(meta)
- Each skill.md contains: triggers, steps, verification rules, evolution stats
- Auto-evolution thresholds: ≥3 successes auto-creates L0 skill; ≥10 uses + 70% success rate promotes L0→L1; ≥50 uses + 85% promotes L2→L3
- `skills/` directory = the distribution unit; share via git, no special transport
- Runtime stores (vectors, SQLite, memory) are local-only — never distributed
- Skill evolution metadata (`*.meta.json`) is runtime-generated, not in git
- Skill Registry: Register, Promote, Rollback with version history tracking
- Skill Marketplace: MarketplaceClient for cross-project discovery, rating, and installation
- SkillPublisher: git-based skill sharing with GossipDiscovery peer exchange
- SkillExtractor.WriteSkillAsync: writes .md + .meta.json, auto-promotes with file relocation

## Four-Pillar .md Asset System
- Skills (skills/*.md), Memory (memory/*.md), Prompts (prompts/*.md), Tools (tools/*.md) share the same .md format + Loader + Service pattern
- 115+ .md tools across 12 domains: git, vfs, gis, eia, code, system, integration, management, discovery, doc, memory, cli
- 5 tool types: Shell, Http, Compose, Prompt, Service
- MdToolBridge: auto-wrapping — MD tool first, C# handler fallback
- 11 config/*.md OptionService sections managing 61+ keys via unified env→config→default chain

## Multi-Agent Coordination
- LTAICoordinator: RunTeam(team, goal) → auto-decompose → TaskQueue DAG → AgentPool parallel execution
- AgenticLoop: implements Read→Think→Edit→Run→Observe cycle with Part streaming
- AgentProfile: Role Card permissions (plan, build, chat modes)
- Subagent spawning: SpawnSubagentAsync / SpawnSubagentsParallelAsync with isolated sessions

## Prompt System
- PromptService: SelectBest (trigger/tag matching) + Render ({{variables}}) + ComposeAsync (multi-template)
- PromptAbTestManager: Epsilon-Greedy, Thompson Sampling, UCB1 algorithms for prompt optimization
- SystemPromptAssembler: 7-layer assembly — AGENTS.md → Mode → Environment → Skills → Task → Diagnostics → Memory

## Streaming & Messaging
- Part polymorphic message model: TextPart, ReasoningPart, ToolInvocationPart, FilePart, AgentPart
- PartAssembler: state machine for streaming → Part[] conversion
- PartStreamStore: JSONL persistence with SSE replay
- NormalizingChatClient: provider-agnostic Part[] output

## Git Workflow
- Commit only when explicitly asked
- Write commit messages in English, present tense, max 72 chars
- Commit message format: `type: brief description`
- Types: fix/feat/refactor/perf/test/docs/chore
- Before committing: `git diff --cached` to review what's being committed
- Tracked artifacts include .md asset files: config/, prompts/, memory/, skills/

## Tool Call Policy
- ShellTools: commands piped via stdin, never embedded in shell arguments
- FileSystemTools: all paths resolved relative to workspace root
- GitTools: prefer `git diff` over `git status` for showing changes
- HttpTools: no requests to internal IPs (10.x, 172.16-31.x, 192.168.x, 127.x)
- CodeAct Hyperlight micro-VM sandbox: fallback execution for untrusted code
- md: prefix: invoke .md-defined tools from tools/ directory

## Rule System
- Safety rules: NRules-inspired pattern matching engine
- Rules loaded from PolicyAsCode YAML + compiled to RETE-style network
- Rule format: `when { conditions } then { actions }`
- Priority: lower number = higher priority
- Canary: new rules test on x% traffic before full rollout

## Knowledge Graph
- Entities and relations stored in SQLite with FTS5
- Predictability Index (PI) from PNAS 2026 paper: assess graph reliability
- PI < 0.6 → prefer vector search over graph traversal
- Node2Vec embeddings for entity similarity (planned)

## Model Routing
- L0: local ONNX/GGUF for embedding + lightweight tasks
- L1: fast cloud model (deepseek-v4-flash / qwen-turbo)
- L2: deep cloud model (deepseek-v4-pro / qwen-max)
- Degradation chain: L2→L1→L0 on budget/circuit-break

## Error Handling
- NEVER swallow exceptions silently — log at minimum
- Tool call failures: retry with exponential backoff (max 3)
- Streaming failures: return partial response + error indicator
- Circuit breaker: 5 consecutive failures → 30s cooldown
- FixInstinctStore: successful fix patterns learned and reused

## Testing Convention
- Test files in tests/ directory, mirroring src/ structure
- Test methods named: `{Category}_{Number}_{Description}`
- xUnit: all tests must pass before commit
- Benchmark: BenchmarkDotNet in LTAI.Benchmarks
- TestResultParser: structured ingestion of dotnet test, JUnit XML, and JSON results
- AgenticLoop auto-runs tests after `dotnet build` on code changes
