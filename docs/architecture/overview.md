# LTAI Architecture Overview

## Layered Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                       UI Layer                               │
│  TUI (Spectre.Console) │ Desktop (Avalonia) │ Web (ASP.NET)  │
├─────────────────────────────────────────────────────────────┤
│                       CLI Layer                              │
│     LTAI.Cli — mcp-server, agents list/show, ltai run       │
├─────────────────────────────────────────────────────────────┤
│                     Agent Layer                              │
│  ChatAgent │ DecisionTreeRouter │ AgentWorkflows              │
│  BackgroundAgents │ HarnessAgent │ FallbackAgent             │
│  80+ AITools │ AIContextProviders │ PipelineConfig           │
├─────────────────────────────────────────────────────────────┤
│                      AI Layer                                │
│  MultiProviderChatClient (router + circuit breaker)          │
│  OpenAIClient │ AnthropicClient                              │
│  EmbeddingClient (ONNX → API → BM25)                        │
│  LocalEmbedder (batched ONNX + GPU EP)                      │
│  ToolEmbeddingCache │ RemoteEmbeddingCache                   │
├─────────────────────────────────────────────────────────────┤
│                    Core Layer                                │
│  LTAIOptions │ SecretManager (DPAPI encrypted)              │
│  UsageTracker │ SafetyRules │ PathUtils                      │
│  CircuitBreakerStore (SQLite persisted)                     │
│  OTel Tracing/Metrics                                       │
├─────────────────────────────────────────────────────────────┤
│                   Infrastructure Layer                       │
│  KgStore (SQLite WAL + FTS5) │ CgGraph │ KbGraph             │
│  WasmtimeSandbox │ MCP Client/Server │ TaskQueue             │
│  DurableTask (DTFx in-process) │ YAMLWorkflowRegistry        │
└─────────────────────────────────────────────────────────────┘
```

## Key Architecture Decisions

| Decision | Rationale |
|---|---|
| MAF (Microsoft Agent Framework) | Standardized agent protocol, hosting, workflows |
| SQLite WAL + FTS5 | Zero-dependency vector/graph store, embeddable |
| ONNX embedding (batched) | 5-10x throughput vs sequential, GPU support |
| Circuit breaker SQLite persistence | Cross-restart fault isolation |
| Per-agent FallbackAgent | Single agent failure doesn't crash host |
| YAML/JSON hot-reload workflows | Edit orchestration without recompiling |

## Data Flow

```
User Input → ChatAgent → DecisionTreeRouter → AgentWorkflows
                                                ├─ Greeting (YAML fast path)
                                                ├─ Sequential/Concurrent
                                                └─ BackgroundAgents delegation
                                                    → LTAI-Math, LTAI-Code, etc.
```
