# LTAI .NET Migration Plan

## Status

| Phase | Status | Description |
|-------|:------:|-------------|
| Core (LTAI.Core) | Done | ICognitiveMesh, IToolRegistry, ILayerGovernor, IProviderEngine, Handshake/Journal models, LTAIOptions |
| AI (LTAI.AI) | Done | 10 Governors + ProviderEngine (OpenAI SSE) + LivingTreeSystem + SystemGuardian |
| Web (LTAI.Web) | Done | POST /api/chat, GET /api/status, GET /api/health + Token bucket rate limit |
| Vector (LTAI.Vector) | Done | IVectorStore, VectorStore (ConcurrentDictionary), Embedding (Local/API), DocumentStore (SQLite+FTS5+RRF), KnowledgeBase, KnowledgeGraph, RelationEngine, StructMemory, Reranker, QueryDecomposer, AgenticRAG |
| Browser (LTAI.Browser) | Done | PuppeteerSharp headless Chromium, 3-tier nav, ARIA page state extraction, AdaptiveExtractor, session pool |
| Document (LTAI.Document) | Done | UniversalFileParser (13 magic-byte), 9 parsers: Json/Xml/Csv/Text/Markdown/INI/YAML-TOML/HTML/Log |
| Network (LTAI.Network) | Done | IP2PNode, Channel<NetworkMessage>, ServiceDiscovery (HTTP), peer registry |
| Host (LTAI.Host) | Done | Program.cs, appsettings.json, all services wired, 5 tools registered to IToolRegistry |
| Integration | Done | ContextGovernor IVectorStore injection, knowledge preloading |
| Knowledge Layer | Done | DocumentStore (SQLite+FTS5+RRF), KnowledgeBase, KnowledgeGraph, RelationEngine (BF S推理), StructMemory (TemporalCompressor+SignalCleaner), Reranker (Jaccard+struct), QueryDecomposer (CN split), AgenticRAG (iterative+circuit breaker) |

## .NET AI Ecosystem Reference

### Microsoft Stack
- **Microsoft.Extensions.AI** — Core abstractions (IChatClient, IEmbeddingGenerator), model-agnostic
- **ML.NET** — Traditional ML / AutoML
- **Semantic Kernel (SK)** — Official LLM orchestration SDK (plugins, memory, planners)
- **Microsoft Agent Framework** — Multi-agent orchestration (sequential, group, handoff), merging SK + AutoGen
- **VectorData** — Vector store abstraction layer
- **DataIngestion** — RAG preprocessing pipeline

### Open-Source Projects
- AutoGen.NET — Multi-agent conversations
- Kernel Memory — RAG pipeline with automatic ingestion
- LlamaSharp — Local inference with llama.cpp bindings
- OllamaSharp — Ollama API .NET client
- BotSharp — AI agent platform
- AntSK — Local-first SK + Ollama integration

### LTAI Alignment
Current self-built architecture (CognitiveMesh + ProviderEngine) aligns with Microsoft.Extensions.AI abstraction pattern. Progressive integration paths:
1. Replace ProviderEngine with IChatClient (M.E.AI)
2. Adopt Semantic Kernel for advanced planning patterns
3. Use VectorData abstraction for pluggable vector backends

## File Count

| Project | .cs Files |
|---------|:---------:|
| LTAI.Core | 13 |
| LTAI.AI | 13 |
| LTAI.Web | 3 |
| LTAI.Vector | 17 |
| LTAI.Browser | 5 |
| LTAI.Document | 5 |
| LTAI.Network | 5 |
| LTAI.Host | 2 |
| **Total** | **65** |

## Build Status
- All 7 projects: 0 errors, 0 warnings
- Tests: 27 passed, 0 failed
- Target: .NET 10
