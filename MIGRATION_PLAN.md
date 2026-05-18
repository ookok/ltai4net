# LTAI .NET 迁移进度

> 源仓库: https://github.com/ookok/LivingTreeAlAgent (Python ~700模块)  
> 目标仓库: https://github.com/ookok/ltai4net (NET 10)  
> 更新: 2026-05-18

## 当前状态

| 指标 | 值 |
|------|-----|
| 项目数 | 12 |
| .cs 文件 | ~130 |
| 构建 | 0 错误 |
| 测试 | 27 通过 |

## 技术选型

| 层 | 技术 | 依据 |
|----|------|------|
| 运行时 | .NET 10 | 最高性能，AOT支持 |
| LLM网关 | 自研 ProviderEngine | OpenAI兼容，28厂商 |
| Agent框架 | 自研 CognitiveMesh + 11 Governors | 仿生神经系统 |
| Web | ASP.NET Core Minimal API | 微软生态 |
| 浏览器 | PuppeteerSharp | Chrome控制 |
| HTML | HtmlAgilityPack | XPath选择器 |
| DB | Microsoft.Data.Sqlite + FTS5 | 全文搜索 |
| 向量 | 内存 Cosine TopK | 快速验证 |
| 嵌入 | 字符哈希 LocalEmbeddingBackend | 384维 |
| 序列化 | System.Text.Json | AOT友好 |
| P2P | Channel<NetworkMessage> | 异步消息 |
| 测试 | xUnit | .NET标配 |

## 模块映射

### ✅ 已完成 (12 项目)

| Python 子系统 | .NET 项目 | .cs | 状态 |
|---------------|-----------|-----|------|
| `core/interfaces` | `LTAI.Core` | 15 | ✅ 接口层+配置+EIAModels+ContextFolding |
| `treellm/governors` | `LTAI.AI` | 16 | ✅ 11 Governors + ProviderEngine + LivingTreeSystem + GovernorUtilities |
| `api/` | `LTAI.Web` | 3 | ✅ 3端点 + 令牌桶限流 |
| `knowledge/` | `LTAI.Vector` | 17 | ✅ VectorStore+Embedding+DocumentStore(FTS5)+KnowledgeBase+AgenticRAG+Reranker+KG+StructMem |
| `capability/browser` | `LTAI.Browser` | 5 | ✅ PuppeteerSharp + AdaptiveExtractor |
| `capability/parser` | `LTAI.Document` | 6 | ✅ UniversalFileParser + 9 parsers (JSON/XML/CSV/Text/MD/INI/YAML/HTML/Log) |
| `network/` | `LTAI.Network` | 5 | ✅ P2PNode + ServiceDiscovery |
| `treellm/routing` | `LTAI.TreeLLM` | 10 | ✅ 6路由策略 + 14维HolisticElection + ElectionBus + CircuitBreaker + CoherenceGate + SemanticCache + AutoPrompt + StrategicDistiller |
| `execution/` | `LTAI.Execution` | 10 | ✅ TaskTree + ReactExecutor(47工具) + DAGExecutor + BatchExecutor + Orchestrator + QualityChecker(9步) + QualityScorer + SelfHealer + RecursiveDecomposer |
| `memory/` | `LTAI.Memory` | 7 | ✅ UserModel(L1-L3) + PersonaMemory + EmotionalMemory(Plutchik) + MemoryPolicy(MemPO) + MemoryOrchestrator + TraitEvolution |
| `economy/` | `LTAI.Economy` | 5 | ✅ Metabolism(12器官) + EconomicEngine(ROI+合规) + ThermoBudget(KL级联) + GRPO(3合1) + InverseReward |
| — | `LTAI.Host` | 1 | ✅ Program.cs + appsettings.json |

### 🟡 待迁移

| Python 子系统 | 文件 | 优先级 |
|---------------|------|--------|
| `dna/` | 124 | 🔴 高 (意识/自进化/安全/激素/生命引擎) |
| `capability/` deep | 80+ | 🟡 中 (代码引擎/搜索/技能/管道) |
| `reasoning/` | 30+ | 🟡 中 |
| `observability/` | 18 | 🟡 中 |
| `integration/` | 16 | 🟢 低 |
| `optimization/` | 4 | 🟢 低 |
| `serialization/` | 5 | 🟢 低 |

### ❌ 计划复用 (第三方)

| 自研组件 | → 替换方案 |
|----------|-----------|
| IProviderEngine | Microsoft.Extensions.AI `IChatClient` |
| IVectorStore | Microsoft.Extensions.VectorData |
| DocumentStore | Kernel Memory |
| LivingTreeSystem | MAF AIAgent + Workflow |
| CircuitBreaker | Polly |
| RateLimiting | ASP.NET Core Rate Limiter |

## 覆盖率

整体 ~25% Python 代码库已 .NET 化。核心治理+知识检索+浏览器+文档解析+执行引擎+记忆层+经济层+路由引擎已就位。DNA(意识/进化/安全)为下一波重点。
