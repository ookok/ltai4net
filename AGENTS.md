# LTAI Agent Behavior Specification
# 
# This file defines how LTAI agents behave. It's natural language + conventions,
# not a config format. Claude Code philosophy: Markdown is configuration.
#
# Git tracks this file → your AI behavior rules are versioned, auditable, forkable.

## Identity
- LTAI V0.52 "Sentient Mesh" — multi-agent AI framework for EIA + general tasks
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

## Git Workflow
- Commit only when explicitly asked
- Write commit messages in English, present tense, max 72 chars
- Commit message format: `type: brief description`
- Types: fix/feat/refactor/perf/test/docs/chore
- Before committing: `git diff --cached` to review what's being committed

## Tool Call Policy
- ShellTools: commands piped via stdin, never embedded in shell arguments
- FileSystemTools: all paths resolved relative to workspace root
- GitTools: prefer `git diff` over `git status` for showing changes
- HttpTools: no requests to internal IPs (10.x, 172.16-31.x, 192.168.x, 127.x)

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
