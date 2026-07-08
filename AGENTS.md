# LTAI 4 Net — Agent 指南

多 Agent 框架，基于 Microsoft Agent Framework (MAF)。3 种前端 (TUI/Desktop/Web) + CLI，11 个 agent（由 `agents/*.agent.md` 定义），本地 ONNX 嵌入，YAML 热改编排。

## ⚡ 零配置启动

```bash
# 1. 复制环境变量模板
copy .env.example .env
# 或:  cp .env.example .env

# 2. 编辑 .env，填入 DEEPSEEK_API_KEY（或任意 LLM API Key）
#    .env 在启动时自动加载，无需手动设置环境变量

# 3. 构建并启动
./run-tui.bat          # TUI 终端界面
./run-web.bat          # Web API (http://localhost:5100)
./run-desktop.bat      # 桌面应用
dotnet run --project src\LTAI.Cli -- health  # CLI 健康检查
```

**只需一个 API Key 即可启动。** 配置默认使用 `deepseek-fast` 提供程序（DeepSeek V4 Flash），模型自动选拔 L2/L3 层。如果 `.env` 不存在，启动脚本会自动从 `.env.example` 创建模板。

## 项目结构

- `src/LTAI.Core/` — 配置、安全、用量追踪（零外部依赖）
- `src/LTAI.AI/` — LLM 路由器 (`MultiProviderChatClient`)、ProviderRegistry、ModelAutoSelector、嵌入 (`LocalEmbedder`)、ToolRegistry
- `src/LTAI.Agent/` — agent 构建、编排、上下文、ToolSet、AgentToolStore、DevUI 服务、持久化
- `src/LTAI.Agent/Format/` — BabelTele 紧凑编码（LLM 对 LLM 压缩格式）
- `src/LTAI.Agent.CodeAnalysis/` — 代码分析（TreeSitter 解析器，语义代码搜索）
- `src/LTAI.Agent.Database/` — 数据库工具
- `src/LTAI.Agent.Documents/` — Office 文档工具
- `src/LTAI.TUI/` — Terminal.Gui 终端 UI (Inline 模式，类 Claude Code/Copilot CLI)
- `src/LTAI.Desktop/` — Avalonia 桌面 UI（内嵌 PseudoTerminal: ConPTY/forkpty）
- `src/LTAI.Web/` — ASP.NET Minimal API (端口 5100)
- `src/LTAI.Cli/` — CLI 工具 (`ltai`)
- `src/LTAI.Accelerator/` — 独立加速器（非核心 agent 链）
- `src/LTAI.Hpo/` — 超参优化引擎（Samplers, Pruners，独立项目）
- `src/LTAI.Mm/` — MetaMessage 记忆模块
- `src/LTAI.Agent.Eia/` — EIA 集成
- `src/Shared/Polyfill.cs` — 跨项目 Polyfill
- `extern/agent-framework/` — MAF git 子模块 (Microsoft.Agents.AI)
- `extern/durabletask-dotnet/` — DTFx git 子模块 (源码参考)
- `extern/Terminal.Gui/` — Terminal.Gui git 子模块 (gui-cs, 预编译 DLL 到 `dist/lib/terminal.gui/`)
- `extern/Editor/` — Terminal.Gui.Editor git 子模块 (gui-cs, 预编译 DLL 到 `dist/lib/editor/)`
- `models/` — models-dev-providers.json（8 provider × 560+ 模型元数据缓存）

## DI 注册顺序（必须保持）

```csharp
services.AddLTAICore();     // 配置、安全、日志
services.AddLTAIAI();       // LLM 路由器、嵌入
services.AddLTAIAgent();    // 12 agents（11 领域 + LTAI-Router）、编排、工具
```

每个 agent 通过 `AgentDefinitionLoader.GetAgentDefinitions()` 读取 `agents/*.agent.md` 注册为 MAF keyed service。ProviderRegistry 和 ModelAutoSelector 在 DI 启动时自动初始化。

以下 DI singleton 已注册供组件间共享：

| 服务 | 接口 | 说明 |
|------|------|------|
| `AgentRegistry` | `IAgentRegistry` | Agent 定义加载 + 语义路由嵌入 |
| `ToolRegistry` | `IToolRegistry` | 工具 BM25+向量+RRF 双路检索 |
| `PromptLoader` | `IPromptLoader` | 提示词文件加载 + FileSystemWatcher 热重载 |

## Agent 定义

Agent 由 `agents/*.agent.md` YAML front-matter 声明。11 个 agents：`LTAI-Chat`、`LTAI-Dev`、`LTAI-Data`、`LTAI-QA`、`LTAI-Ops`、`LTAI-Math`、`LTAI-System`、`LTAI-Writer`、`LTAI-Arch`、`LTAI-Office`、`LTAI-Explore`（另含 L2 路由 agent `LTAI-Router`）。

```bash
ltai agents list          # 一览
ltai agents show <name>   # 详细 prompt + 工具 + 权限
```

### FastContext 范式：委托探索

`LTAI-Explore` 受微软 FastContext（arXiv 2606.14066）启发，将**仓库探索**与**任务求解**分离。主 agent 通过 `BackgroundAgents_StartTask` 或 `Explore` 子代理工具，将探索查询委托给 `LTAI-Explore`，返回紧凑的 `<final_answer>` 引用块：

```
<final_answer>
src/router.py:42-58     # 关键逻辑
tests/test_router.py:101-119  # 相关测试
</final_answer>
```

**核心优势**：探索 token 不出现在主 agent 上下文窗口→ 主 agent token 可降 **~60%**。

**ExploreToolSet**（`src/LTAI.Agent/Tools/ExploreToolSet.cs`）提供只读的紧凑引用工具，利用现有的 `FileSystemTools`（ReadFileContent、Glob）和 `SearchTools`（SearchContent），输出 XML 标签化的 `<citation>` / `<file-list>` / `<search-results>` 格式。`LTAI-Explore` agent 默认注册这些工具，其他 agent 可通过 `tools: [explore]` 启用。

## Prompt 架构

系统 prompt 分三层拼装：

```
Layer 0: agents/system-{lang}.prompt.md  ← 公共基础（身份/风格/策略/验证）
Layer 1: AgentPromptBuilder.cs            ← C# fallback + 语言切换
Layer 2: agents/*.agent.md (正文)        ← 领域专属工作流
```

`system-*.prompt.md` v3 包含以下节：

| 节 | 用途 |
|---|---|
| `<identity>` | 角色身份声明 |
| `<tone-style>` | 输出约束（极简、无 preamble、代码引用格式） |
| `<language>` | 双语切换规则 |
| `<task-execution>` | 任务执行流程（TodoWrite 追踪） |
| `<tool-strategy>` | 工具调用优先级（搜索→读取→编辑链） |
| `<proactiveness>` | 主动性与安全边界 |
| `<code-conventions>` | 代码风格与安全约定 |
| `<tool-usage>` | 工具调用格式约束 |
| `<verification>` | 生成后自动语法检查（3 层：QuickParse + RuleEngine + LSP） |
| `<context-management>` | 上下文主动压缩策略 |

### 生成时语法检查

`GrammarCheckStep`（`Pipeline/Steps/GrammarCheckStep.cs`）在 agent tool 执行后自动运行：

1. **第 1 层** QuickParse — Roslyn/TreeSitter AST 解析（<200ms）
2. **第 2 层** RuleEngine — 确定性规则匹配（<300ms）
3. **第 3 层** CLR (Claim-Level Reliability) — 提取 import/URL/config 关键声明并交叉验证（<300ms，VibeThinker 启发）
4. **第 4 层** LSP — 语义诊断（<500ms）

发现语法错误时：
- 注入错误消息到 agent 上下文（`文件:行号:列号` 格式）
- 设置 `GrammarCheckBlocked` 标志，阻断新任务
- `ChatAgent` 自动重试修复（上限 2 次）

## Pipeline 架构

`PipelineRunner`（`Pipeline/PipelineRunner.cs`）将 12 个 `IPipelineStep` 声明式组装为有序执行链，在 `MessageContext`（线程安全属性包）上顺序执行：

```
执行顺序（可跳过 null step）：
  LoraAdapterStep → MemoryCachingStep(Restore) → RagContextStep
  → ProactiveSuggestStep → SafetyCheckStep → RouterStep
  → ToolExecutionStep → MemoryCachingStep(Save) → CompactionStep
  → GrammarCheckStep → AntiPatternCheckStep → QualityGateStep
  → DoDCheckStep → RetrospectiveStep
```

### 阻断链

步骤可通过 `MessageContext` 标志提前终止管线：

| 标志 | 来源步骤 | 效果 |
|------|---------|------|
| `SafetyBlocked` | SafetyCheckStep | 跳过 RouterStep 及后续，返回安全拦截消息 |
| `GrammarCheckBlocked` | GrammarCheckStep | 阻断新任务，`ChatAgent` 自动重试修复（上限 2 次） |
| `AntiPatternBlocked` | AntiPatternCheckStep | 反模式检查未通过，注入修复指引 |
| `QualityGateBlocked` | QualityGateStep | 质量门禁未通过，触发重新生成 |
| `DoDBlocked` | DoDCheckStep | 完成定义检查失败（含 TODO/FIXME/{{}}模板残留） |

### PipelineRunner DI 注册

```csharp
services.AddSingleton<SafetyCheckStep>();
services.AddSingleton<ToolExecutionStep>();
services.AddSingleton<CompactionStep>();
services.AddSingleton<DoDCheckStep>();
services.AddSingleton<RetrospectiveStep>();
services.AddSingleton<PipelineRunner>();
```

`PipelineRunner` 从 DI 解析所有已注册的 `IPipelineStep`，未注册步骤自动跳过。`ChatAgent` 通过 `PipelineRunner.RunPostGenerationAsync()` 统一执行后处理管线（MemoryCachingSave → Compaction → GrammarCheck → QualityGate → DoDCheck → Retrospective），替代了之前 3 处内联 `new GrammarCheckStep(...)` 的调用方式。

### CompactionStep 集成上下文卸载

`CompactionStep` 在上下文超过 75% 阈值时自动触发 **TencentDB-Agent-Memory 风格上下文卸载**：

1. **Phase A — Offload**：重工具结果（>1KB/>40 行）写入 `.livingtree/refs/*.md`，替换为 `[refs/{file}#{hash}]`
2. **Phase B — 压缩**：原有 TieredCompressor 继续压缩消息
3. **Phase C — Mermaid 注入**：注入 `stateDiagram-v2` + 紧凑状态摘要到上下文

卸载后通过 `[refs/{file}#{hash}]` → refs/index.md → 完整文件 可无损恢复原始内容。

## AgentContextProviderBuilder 实际顺序

`AgentContextProviderBuilder.Build()` 组装 **22 个** `AIContextProvider`（文档注释写 16 个，实际更多），顺序如下：

| 索引 | 提供者 | 类名 | 备注 |
|------|--------|------|------|
| 0 | 技能排名提供者 | `SkillRankingProvider` | 始终第一个 |
| 1 | **安全协调器** | `SafetyCoordinator` | **可选** — 当 `LTAIOptions.AI.SkipSafetyChecks=false` 时在此插入 |
| 1/2 | **内存权限提供者** | `MemoryAuthorityProvider` | 未在文档注释中列出 |
| 2/3 | L0 身份提供者 | `L0IdentityProvider` | 身份声明 |
| 3/4 | L1 基本提供者 | `L1EssentialProvider` | 5 条最近记忆 |
| 4/5 | **规格上下文提供者** | `SpecContextProvider` | 未在文档注释中列出 |
| 5/6 | 压缩提供者 | `CompactionProvider` | MAF 管道压缩 |
| 6/7 | CCR 提供者 | `CCRProvider` | 内容压缩/检索标记 |
| 7-10/8-11 | 知识图谱 | `KbGraph` | 按需 |
| 7-10/8-11 | 代码图谱 | `CgGraph` | 按需 |
| 7-10/8-11 | 代码块索引 | `CodeChunkIndex` | 按需 |
| 7-10/8-11 | WASM 沙盒 | `WasmtimeSandbox` | 按需 |
| 11/12 | L3 按需提供者 | `L3OnDemandProvider` | 任务相关记忆 |
| 12/13 | L4 深度搜索提供者 | `L4DeepSearchProvider` | 语义深度搜索 |
| 13/14 | L6 代理日记提供者 | `L6AgentDiaryProvider` | 日记条目 |
| 14/15 | 来源提供者 | `ProvenanceProvider` | 知识来源追踪 |
| 15/16 | 指令提供者 | `InstructionProvider` | 每个模型的指令提示 |
| 16/17 | 环境提供者 | `EnvironmentProvider` | cwd / OS / 运行时 |
| 17/18 | 技能提供者 | `AgentSkillsProvider` | 技能目录内容 |
| 18/19 | 缓存对齐提供者 | `CacheAlignerProvider` | KV 缓存对齐提示 |
| 19/20 | LSP 诊断提供者 | `LspDiagnosticsProvider` | LSP 诊断 |

> 文档注释与实际差异：`MemoryAuthorityProvider` 和 `SpecContextProvider` 未在 doc 中列出。`ToolRetrievalProvider` 已删除（替换为 `ToolFilteringChatClient` IChatClient 中间件）。文档将 KbGraph/CgGraph/CodeChunkIndex/WasmtimeSandbox 合并为一条，实际是 4 个独立对象。

## 关键命令

```bash
dotnet build LTAI.sln                     # 构建所有项目（含子模块）
dotnet build src/LTAI.TUI                # 仅 TUI
dotnet build src/LTAI.Desktop            # 仅 Desktop
dotnet build src/LTAI.Web                # 仅 Web
./scripts/build-maf.ps1                  # 预编译 MAF 到 dist/lib/maf（加速增量构建）
./scripts/build-terminalgui.ps1          # 预编译 Terminal.Gui 到 dist/lib/terminal.gui
./scripts/dev-setup-submodules.ps1       # 初始化子模块 + sparse-checkout
cd src/LTAI.TUI && dotnet run            # 启动 TUI (Inline 模式，需先 build-terminalgui.ps1)
cd src/LTAI.Desktop && dotnet run        # 启动 Desktop
cd src/LTAI.Web && dotnet run            # 启动 Web → http://localhost:5100
dotnet test tests/LTAI.Tests                           # 运行所有测试
dotnet test tests/LTAI.Tests --filter "Category!=Integration"  # 仅运行单元测试（跳过集成/网络测试）
dotnet run -c Release --project tests/LTAI.Benchmarks  # BenchmarkDotNet
dotnet run --project tests/LTAI.Benchmarks -- smoke    # 快速 smoke test
ltai models show                         # 查看自动选拔的 L1/L2/L3 模型
ltai models set l2 deepseek-chat        # 覆盖 L2 模型
ltai models auto l2                     # 恢复自动选拔
```

## 子模块 & sparse-checkout

首次克隆后必须跑：

```bash
./scripts/dev-setup-submodules.ps1
```

这会把 `extern/agent-framework` 从 251MB 缩到 ~27MB（排除 Python/tests/bin/obj/.dll/.pdb/.cache）。`extern/durabletask-dotnet` 当前 HEAD = `b7216672` (v1.16.2-141, 仅源码参考，不走 ProjectReference)。

> **P0:** 两个 submodule 都跟随 main 分支 — 强烈建议在 `extern/agent-framework` 和 `extern/durabletask-dotnet` 内执行 `git checkout <commit-sha>` 锁版本,避免 `git submodule update` 拉到不兼容 commit。MAF DLL 已预编译到 `dist/lib/maf/`,可通过 `scripts/build-maf.ps1` 重建。

## ONNX 嵌入模型

3 个 Xenova 预量化模型，走 `hf-mirror.com` 镜像：

| 模型 | 默认变种 | 大小 |
|---|---|---|
| MiniLM-L6-v2 | INT8 | 22MB |
| BGE-small-zh | INT8 | 23MB |
| BGE-small-en | INT8 | 32MB |

```bash
dotnet build -t:DownloadEmbeddingModelMiniLM     # 只下 MiniLM INT8
dotnet build -t:DownloadEmbeddingModelBgeSmallZh
dotnet build -t:DownloadEmbeddingModelBgeSmallEn
```

已配远程 API key（DEEPSEEK/OPENAI/SILICONFLOW/DASHSCOPE）时自动跳过 ONNX 加载。GPU 自适应：`LTAI:Embedding:Gpu=auto` 按 DML → CUDA → CPU 探测。

## YAML 热改编排

`.livingtree/workflows/*.yaml|*.json` 可热编辑，保存后 250ms 自动重载（FileSystemWatcher）。支持的编排类型：
- `greeting` — 问候快速通道
- `decision-tree` — 向量路由阈值
- `sequential` / `concurrent` — 管道
- `mcp` — MCP 工具调用（YAML 中 `InvokeMcpTool`）

```bash
TUI: /workflow list | /workflow reload | /workflow show <name>
Web: GET /ltai/v1/workflows
```

## Web 端点

| 端点 | 说明 |
|---|---|
| `GET /health` | 完整健康检查 |
| `GET /ready` | K8s readiness probe |
| `GET /devui` | MAF DevUI（仅 development） |
| `GET /ltai/v1/entities` | 12 agents LTAIAgentCard |
| `GET /ltai/v1/jobs` | 后台任务列表（60s 自动驱逐） |
| `GET /ltai/v1/workflows` | 热改编排配置 |
| `POST /ltai/v1/workflows/reload` | 重载所有编排 |
| `/v1/agents/{name}/responses` | OpenAI Responses API |
| `/v1/agents/{name}/chat/completions` | OpenAI Chat API |
| `/a2a/{name}` | A2A 协议 |
| `/agui/{name}` | AGUI 协议 |

不注册全局 `/v1/responses` 和 `/v1/chat/completions`（与 per-agent 路由冲突）。

## 重要约束

- **Swashbuckle 在 .NET 10 preview 上 TypeLoadException**：用内置 `AddOpenApi()` + `MapOpenApi()`。
- **MAF DevUI 仅在 `IsDevelopment()` 注册**（暴露 system prompt）。
- **OTel console exporter** 默认开启；OTLP 需配置 `LTAI:Telemetry:OtlpEndpoint`。
- **`ShellEnvironmentProvider` 已完全移除**（Windows .NET 10 上启动 PowerShell 进程卡 60+ 秒）。
- **持久化目录**：`.livingtree/`（SQLite 知识图谱 + 会话 + 任务队列）。删除可重置所有状态。
- **配置**：`appsettings.json` `LTAI` 节 + 环境变量（DEEPSEEK_API_KEY 等）。仅需配置一个 API Key，L2/L3 自动选拔。
- **Provider 元数据**：`models/models-dev-providers.json`（252KB，首次启动自动加载，后台 24h 刷新）。

## 端侧推理

`models/edge-providers.json` 配置本地推理工具的 provider 元数据，加载后与远程 provider 合并使用。

### 支持的端侧工具

| Provider ID | 工具 | 说明 |
|---|---|---|
| `ollama` | [Ollama](https://ollama.ai) | 本地 LLM 运行时，支持 GGUF 模型 |
| `vllm` | [vLLM](https://github.com/vllm-project/vllm) | 高性能推理引擎，支持 PagedAttention |
| `llamacpp` | [llama.cpp](https://github.com/ggerganov/llama.cpp) | C/C++ 推理，支持 CPU/GPU 混合 |
| `lmstudio` | [LM Studio](https://lmstudio.ai) | 图形化本地模型管理 |
| `koboldcpp` | [KoboldCPP](https://github.com/LostRuins/koboldcpp) | 面向角色扮演的推理前端 |

### 切换端侧 Provider

在 `appsettings.json` 的 `LTAI:AI` 节设置 `DefaultProvider`：

```json
{
  "LTAI": {
    "AI": {
      "DefaultProvider": "ollama",
      "MaxTokens": 8192,
      "Temperature": 0.7
    }
  }
}
```

支持的 `DefaultProvider` 值：`ollama`、`vllm`、`llamacpp`、`lmstudio`、`koboldcpp`。

Provider 默认可断连（不配置 endpoint 也不影响启动），通过 `models/edge-providers.json` 中的 `api` 字段配置端侧服务地址。

### Ollama + Qwen3-8B 示例配置

`models/edge-providers.json` 已内置 Qwen3-8B 等模型。启动 Ollama 后拉取模型：

```bash
ollama pull qwen3:8b
ollama serve  # 默认 http://localhost:11434
```

应用自动使用 Ollama provider，无需进一步配置。也可在 `appsettings.json` 显式指定：

```json
{
  "LTAI": {
    "AI": {
      "DefaultProvider": "ollama",
      "MaxTokens": 8192,
      "Temperature": 0.7
    }
  }
}
```

如需自定义 endpoint，修改 `models/edge-providers.json` 中 `ollama.api` 字段。

## Lookahead Provider Routing

`LookaheadProviderSelector`（`src/LTAI.Agent/Context/LookaheadProviderSelector.cs`）基于 FlashMemory-DeepSeek-V4 的 LSA 范式，预测当前查询需要哪些上下文 provider，跳过无关的昂贵 provider。

**路由流程：**
1. 从对话消息中提取用户最新查询
2. 关键词匹配 + 可选 MiniLM/GloVe embedding 分类为 code/knowledge/memory/system 等域
3. 向上下文注入 `<provider-route skip="...">` 标记
4. 下游 8 个 provider（KbGraph, CgGraph, CodeChunkIndex, WasmtimeSandbox, L4DeepSearch, L6AgentDiary, ProvenanceProvider, LspDiagnosticsProvider）检查标记并自动跳过

**动态边界缓存**（MGPO 启发）：追踪每个 provider 的 skip 准确率。准确率 > 90% 的 provider 直接跳过分类开销；< 70% 的始终分类；70-90% 的用标准分类逻辑。

## 预计算 Reach Index

`ReachIndex`（`src/LTAI.Agent/Vector/ReachIndex.cs`）在 CgGraph 构建完成后后台运行，预计算所有符号的 depth-3 前向/反向可达性。将影响分析从 O(nodes × edges) 的 CTE BFS 降为 O(1) map lookup。

```bash
TUI: /impact <symbol>     # 分析修改该符号的影响范围
```

`LTAI_REACH_INDEX_MAX_NODES` 和 `LTAI_REACH_INDEX_MAX_EDGES` 环境变量控制超大仓库采样。

## GloVe-50d 零依赖嵌入

`Glove50Embedder`（`src/LTAI.AI/Glove50Embedder.cs`）提供 50 维语义嵌入，无 ONNX 依赖，零下载。内置 ~400 个代码相关词向量 + hash OOV fallback。注册为 `EmbeddingClient` 的 fallback 层（ONNX → Remote API → GloVe-50d → BM25 FastEmb）。

可选下载真实 GloVe-50d 词表（`models/glove50d.gv50`，~2MB），自动从 GitHub Releases 下载：
```bash
./scripts/generate-glove50.ps1
```

## Token 节省追踪

`TokenSavingsTracker`（`src/LTAI.Core/Configuration/TokenSavingsTracker.cs`）追踪 graph/tool lookups 替代直接读文件节省的 token 数。

```bash
TUI: /savings             # 查看 token 节省统计
Web: GET /health          # 返回 token_savings 字段
```

指标：`ltai.tokens.saved`、`ltai.tokens.naive`、`ltai.tokens.actual`（OpenTelemetry）。

## 跨仓库合约匹配

`ContractRegistry`（`src/LTAI.Agent/Vector/ContractRegistry.cs`）+ `ContractWatcher` 自动检测跨仓库 API 合约：HTTP 路由、gRPC 服务、消息主题、环境变量、OpenAPI 规范。

```bash
TUI: /contracts           # 查看所有合约
Agent: ListContracts       # agent 工具
Agent: FindCrossRepoContracts  # 跨仓库查询
```

文件变更后 1.5s debounce 自动增量扫描。

## 紧凑图格式

`CompactGraphFormatter`（`src/LTAI.Agent/Vector/CompactGraphFormatter.cs`）GCX1 启发，比 JSON 少 ~27% token。通过 `CgGraph.QueryCompactAsync()` 使用，agent 工具 `QueryCodeGraph` 默认使用此格式。

## Memory Refinery（MeMo 启发）

`MemoryRefinery`（`src/LTAI.Agent/Memory/MemoryRefinery.cs`）后台服务每 15 分钟运行 MeMo 五步合成管线：

1. **Fact Extraction** — 提取直接+间接事实
2. **Consolidation** — 合并相关 QA 对
3. **Verification** — 确保自包含性
4. **Entity Surfacing** — 生成反向 QA（逆转诅咒缓解）
5. **Cross-doc Synthesis** — 连接相关记忆

合成结果存入 `reflection` room，供 L4DeepSearchProvider 检索时使用。

## Entity Surfacing（逆转诅咒）

`PalaceStore.StoreAsync` 每次写入记忆时自动提取大写命名实体，生成反向 QA 对（"Who is X" + "What does X do"）存入 `{room}.entity` room。L4DeepSearchProvider 的多轮查询协议会同时检索正向 + 反向记忆。

## Experience Replay（VibeThinker 启发）

`ExperienceReplayPool`（`src/LTAI.Agent/Memory/ExperienceReplayPool.cs`）离线自我蒸馏：记录成功 agent 交互轨迹 → 按学习潜力评分采样 → 合成为 system prompt 注入。作为 `PalaceFeedbackTracker` 的补充。

## Long2Short token 效率追踪

`Long2ShortTracker`（`src/LTAI.Agent/Tools/Long2ShortTracker.cs`）追踪每个工具的 token 效率。零和 brevity 奖励：同等质量下的较短输出获得更高评分。

## 多维质量门禁（garden-skills Critique 启发）

`QualityGateStep`（`src/LTAI.Agent/Pipeline/Steps/QualityGateStep.cs`）使用 5 维度评分替代单一分数：

| 维度 | 说明 | 评分范围 |
|------|------|---------|
| 哲学一致 | agent 定位与用户意图对齐度 | 0-10 |
| 内容完整 | 是否覆盖任务所有需求 | 0-10 |
| 清晰结构 | 分段、标题、可读性 | 0-10 |
| 工艺质量 | 反 AI 俗套、无模板残留、无 TODO | 0-10 |
| 工具使用 | 工具调用适当性 | 0-10 |

Pipeline 阻断时输出各维度分数供 LLM 自动修复。

## AntiPatternCheckStep（反模式检查）

`AntiPatternCheckStep`（`src/LTAI.Agent/Pipeline/Steps/AntiPatternCheckStep.cs`）在 GrammarCheck 之后、QualityGate 之前运行。扫描三类反模式：

- **文本俗套**：AI 开场白 (`Let me`/`我来`)、emoji 滥用、过度客套、紫粉渐变、模板残留
- **代码反模式**：React `const styles = {...}` 全局污染、`scrollIntoView`、CSS 剪影替代真实产品图、合并冲突标记
- **安全模式**：硬编码 API Key (`sk-`/`ghp_`/`AIza`)、硬编码 secret、`localhost` URL

阻断管线时注入详细修复指引。

## 风格配方系统

`recipes/` 目录提供可复用的写作风格配方（garden-skills style-recipes 启发）：

| 配方 | 最佳用途 |
|------|---------|
| `technical-blog.recipe.md` | 技术博客、教程 |
| `release-note.recipe.md` | 版本发布说明 |
| `changelog.recipe.md` | 多版本变更日志 |
| `api-doc.recipe.md` | API 参考文档 |
| `incident-report.recipe.md` | 事故复盘 |

Agent 通过 YAML front-matter `recipes: [technical-blog, release-note]` 声明所用配方。配方热加载（与 agent 定义相同的 FileSystemWatcher 机制）。

## Agent 版本化与 Manifest

Agent YAML front-matter 新增三个字段：

```yaml
version: 1.0.0              # semver 版本号
manifest: https://...        # 发布产物地址
recipes: [technical-blog]    # 引用风格配方
```

同 `agents/*.agent.md` 热重载机制，修改后自动更新。

## 工具 Agent 工具

以下工具通过 `Builder.Tools.cs` 自动注册到 chat/code/review 等 agent：

| 工具 | 函数名 | 来源 |
|------|--------|------|
| 符号影响分析 | `QueryImpact` | `ReachIndex` |
| 紧凑代码搜索 | `QueryCodeGraph` | `CgGraph.QueryCompactAsync` |
| 跨仓库合约 | `ListContracts` | `ContractRegistry` |
| 跨仓库合约查找 | `FindCrossRepoContracts` | `ContractRegistry` |
| 预览编辑 | `ApplyPatches dryRun=true` | `PatchEditTool` |

## 上下文卸载系统（TencentDB-Agent-Memory 启发）

`ContextOffloader`（`src/LTAI.Agent/Memory/ContextOffloader.cs`）+ `MermaidStateTracker`（`src/LTAI.Agent/Memory/MermaidStateTracker.cs`）实现 Mermaid Symbolic Memory + Context Offload 范式：

| 组件 | 文件 | 说明 |
|------|------|------|
| `ContextOffloader` | `Memory/ContextOffloader.cs` | 将重工具结果卸载到 `refs/*.md`，替换为 `[refs/{file}#{hash}]` 引用 |
| `MermaidStateTracker` | `Memory/MermaidStateTracker.cs` | 构建 `stateDiagram-v2` 状态图，带 clickable node_id 引用 |
| `CompactionStep` | `Pipeline/Steps/CompactionStep.cs` | 压缩前自动卸载 → Mermaid 图注入 |

**工作流：**
1. 上下文超过 75% 阈值时触发
2. 重工具结果（>2KB 或 >40 行）自动卸载到 `.livingtree/refs/{traceId}-{seq}-{tool}.md`
3. 工具结果替换为 `[refs/{file}#{hash}]`（含 SHA256 前 12 字符校验）
4. `MermaidStateTracker` 构建 `stateDiagram-v2`，每个节点含 clickable refs 链接
5. 生成 `{traceId}-index.md` 索引文件作为中间层
6. 压缩后的消息 + Mermaid 图 + 紧凑状态摘要替换原始上下文

**钻取路径（损失less traceability）：**
```
Mermaid 图（上下文中）→ refs/index.md（中间索引）→ refs/{traceId}-*.md（完整内容）
```

**预期效果：** 61% token 减少（TencentDB 报告），白盒可调试性。

### ContextOffloader 阈值

| 常量 | 值 | 说明 |
|------|-----|------|
| `MaxInlineBytes` | 1024 | 超过 1KB 自动卸载 |
| `MaxInlineLines` | 40 | 超过 40 行自动卸载 |
| `MaxInlineChars` | 2048 | 超过 2K 字符自动卸载 |

### 注册

```csharp
services.AddSingleton<ContextOffloader>();
services.AddSingleton<MermaidStateTracker>();
```

## L2/L3 语义金字塔（PalaceStore）

`PalaceStore`（`src/LTAI.Agent/Memory/PalaceStore.cs`）新增 TencentDB-Agent-Memory 启发的语义金字塔，将记忆组织为四层：

| 层 | 名称 | 存储 | 方法 |
|----|------|------|------|
| L0 | 原始会话 | `palace` 表（role=user/assistant） | `StoreAsync` |
| L1 | 原子事实 | `reflection` room | `MemoryRefinery` |
| L2 | 场景块 | 动态分组（entity overlap + time proximity） | `BuildScenarioBlocksAsync(wing)` |
| L3 | 人物画像 | 从场景提取 expertise/themes | `ExtractPersonaAsync(wing)` |

### 场景分组策略（`BuildScenarioBlocksAsync`）

两个 L1 事实属于同一场景块当：
1. **实体重叠**：共享 ≥1 个大写命名实体
2. **时间接近**：创建时间差 < 5 分钟
3. **嵌入相似度**：cosine similarity > 0.7（预留，当前使用实体+时间）

### 人物画像提取（`ExtractPersonaAsync`）

从 L2 场景块提取：
- **Expertise**：出现频次最高的实体（capabilities）
- **Themes**：场景主题去重列表
- **Metrics**：平均重要性、总 facts 数、最后活跃时间

### 白盒 Markdown 导出

```csharp
var l2Markdown = await palace.ExportL2ToMarkdownAsync("coding");
var l3Markdown = await palace.ExportL3ToMarkdownAsync("coding");
// 结果写入 .livingtree/refs/ 供调试查看
```

## 参考文档

- `docs/architecture.md` — 六层架构图
- `docs/ops/runbook.md` — 操作手册
- `docs/maf-paradigm-evaluation.md` — MAF 范式评估

## 环境变量参考

所有环境变量遵循 `LTAI_<DOMAIN>_<PARAM>` 命名规范，默认值匹配原有硬编码。

### 并发控制

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `LTAI_SHELL_CONCURRENCY` | 8 | SafeShellTool 全局并发上限 |
| `LTAI_WASM_CONCURRENCY` | 6 | WasmtimeSandbox 全局并发上限 |
| `LTAI_MOA_CONCURRENCY` | 6 | MoAWorkflow 编排节流 |
| `LTAI_WORKFLOW_CONCURRENCY` | 6 | AgentWorkflows 编排节流 |
| `LTAI_JOB_MAX_CONCURRENT` | 10 | BackgroundJobService 最大并发作业数 |
| `LTAI_SEARCH_MAX_DOP` | min(CPU,4) | SearchTools 并行搜索度 |
| `LTAI_ISSUE_DETECTOR_MAX_DOP` | 4 | IssueDetectors 并行度 |
| `LTAI_TASK_QUEUE_MAX` | 100000 | TaskQueue 有界队列容量 (0=无界) |

### 超时控制

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `LTAI_SHELL_TIMEOUT_SEC` | 30 | WasmtimeSandbox shell 命令超时 |
| `LTAI_WASM_TIMEOUT_SEC` | 60 | WasmtimeSandbox WASM 执行超时 |
| `LTAI_SCRIPT_TIMEOUT_SEC` | 60 | SkillScriptRunner 脚本超时 |
| `LTAI_JOB_PROCESS_TIMEOUT_SEC` | 300 | BackgroundJobService 进程超时 |
| `LTAI_REGEX_TIMEOUT_MS` | 1000 | FileSystemTools/SearchTools 正则超时 |
| `LTAI_SQLITE_BUSY_MS` | 5000 | KgStore SQLite busy_timeout |
| `LTAI_RETRY_BACKOFF_SEC` | `1,2,4,8,16` | RetryQueueWorker 退避序列（逗号分隔） |

### 资源限制

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `LTAI_TOOL_MAX_OUTPUT_BYTES` | 102400 | WasmtimeSandbox 输出截断上限 |
| `LTAI_JOB_MAX_OUTPUT_CHARS` | 100000 | BackgroundJobService 输出截断上限 |
| `LTAI_JOB_EXPIRATION_SEC` | 60 | BackgroundJobService 作业驱逐时间 |
| `LTAI_SQLITE_MMAP_MB` | 256 | KgStore SQLite mmap_size (MB) |
| `LTAI_WASM_MODULE_CACHE_MAX` | 32 | WasmtimeSandbox 模块缓存上限 |
| `LTAI_HTTP_MAX_CONN` | 6 | LLM HTTP 连接池每服务器最大连接 |
| `LTAI_HTTP_POOL_LIFETIME_MIN` | 10 | LLM HTTP 连接池生命周期 (分钟) |
| `LTAI_WATCHER_BUFFER` | 65536 | FileSystemWatcher 内部缓冲区大小 |

### 缓存与间隔

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `LTAI_LLM_CACHE_TTL_MIN` | 5 | MultiProviderChatClient LLM 响应缓存 TTL |
| `LTAI_COMPRESSION_MAX_AGE_DAYS` | 30 | CompressionStore 条目最大保留天数 |
| `LTAI_CG_CACHE_SIZE` | 100 | CgGraph 查询缓存条目数 |
| `LTAI_CG_CACHE_TTL_SEC` | 30 | CgGraph 查询缓存 TTL |
| `LTAI_MEMORY_CONSOLIDATION_MINUTES` | 30 | MemoryConsolidationService 执行间隔 |
| `LTAI_MEMORY_REFINERY_MINUTES` | 15 | MemoryRefinery 反射合成执行间隔 |
| `LTAI_REACH_INDEX_MAX_NODES` | -1 | ReachIndex 采样节点上限（-1=无限制） |
| `LTAI_REACH_INDEX_MAX_EDGES` | -1 | ReachIndex 采样边上限（-1=无限制） |
| `LTAI_COMPRESS_BOOST_*` | 见代码 | 对话类型压缩比 boost 覆盖 |
| `LTAI_RATE_LIMIT_CLEANUP_MIN` | 5 | RateLimitMiddleware 清理间隔 |

### 行为控制

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `LTAI_GREETING_MAX_LENGTH` | 15 | QueryClassifier 问候判定最大字符数 |

## BabelTele 紧凑编码

`BabelTeleFormatter`（`src/LTAI.Agent/Format/BabelTeleFormatter.cs`）实现 arXiv:2606.19857 启发的 LLM 对 LLM 紧凑编码。牺牲人类可读性换取 token 效率，通过自解释 pattern 让 LLM 首次遇到时理解格式。

**5 种类型标记：**

| 标记 | 示例 | 说明 |
|------|------|------|
| `[T:tool#N]` | `[T:ReadFileContent#1] /src/foo.cs L42` | 工具结果 + 首行摘要 |
| `[G:q n=N]` | `[G:searchSymbol n=5 f:c@~src/a.cs]` | 图查询结果 + 节点编码行 |
| `[S:p m=N f:Ln]` | `[S:main m=12 src/bar.cs:L5]` | 搜索结果 + 匹配数 + 首文件 |
| `[R:path#Ln]` | `[R:src/a.cs#L42 r=0.85]` | 文件引用 + 行号 + 相关度 |
| `[E:code Ln:c]` | `[E:CS1001 L5:10 缺少标识符]` | 编译/语法错误 |

每种类型首次出现在 context 窗口时附带展开说明，后续仅用 ~40 字符的紧凑引用。`TokenSavingsTracker` 自动计算节省的 token。

### 集成点

- **`CompactGraphFormatter.FormatBabelTele()`** — 图结果 ~50% token 减少（对比 JSON）
- **`ContextOffloader.OffloadToolCallsAsync()`** — 工具结果卸载生成 `[T:tool#seq] 摘要 [refs/{file}#{hash}]` 双格式
- **`BabelTeleFormatter.ResetForContext()`** — 新会话开始时调用，重置自解释状态

## GateMem 记忆治理

受 arXiv:2606.18829 GateMem 基准启发，在多用户共享记忆场景中实现三个维度：Utility、Access Control、Forgetting。

### 访问控制模型

每个 `MemoryFact` / PalaceStore drawer 有三个 scope 级别：

| Scope | 可见性 | 说明 |
|-------|--------|------|
| `shared` | 所有用户 | 默认，全员可见 |
| `private` | 仅创建者（principal）| 其他用户查询不到 |
| `role:*` | 特定角色 | 预留，按角色过滤 |

### 遗忘机制

```csharp
// 精确删除
await store.ForgetAsync(new MemoryForgetRequest(FactId: "abc123"));

// 按用户批量删除
await store.ForgetAsync(new MemoryForgetRequest(Principal: "user1"));

// 按 room 批量删除
await store.ForgetAsync(new MemoryForgetRequest(Room: "secret"));

// 自动过期清理（后台调用）
await store.PurgeExpiredAsync();
```

### 实现

- `IMemoryStore` 接口新增 `ForgetAsync` + `PurgeExpiredAsync`
- `PalaceStore` SQL schema 增加 `principal`/`scope` 列 + 索引
- `HybridSearchAsync` SQL 级 scope 过滤：`(scope='shared' OR (scope='private' AND principal=@p))`
- `MemoryAuthorityProvider` 注入访问控制 + 遗忘规则到 AI context
- `MemoryStore.SearchFactsAsync` 按 `MemoryFilter.Principal`/`Scope` 自动过滤

## Agentic Abstention（arXiv 2606.28733 CONVOLVE）

`AbstentionCheckStep`（`src/LTAI.Agent/Pipeline/Steps/AbstentionCheckStep.cs`）在管线并行组 4 运行，使用 CONVOLVE 风格 6 条停止规则：

| 规则 | 条件 | 示例 |
|------|------|------|
| R1 重复调用 | 连续 ≥2 次相同工具+参数 | `ReadFileContent(path=X) → ReadFileContent(path=X)` |
| R2 空结果 | 连续 ≥3 次返回空 | 所有工具输出空白 |
| R3 连续错误 | 连续 ≥2 次含 error/exception/failed | 工具调用全部失败 |
| R4 文件循环 | 同一文件读 ≥4 次未写 | 只读不写 |
| R5 工具单一 | 某工具占比 ≥70% 且 ≤2 种类型 | 90% 调用是 `ReadFileContent` |
| R6 管线错误 | `PipelineError` 非空 | 上游步骤异常 |

阻断时设置 `AbstentionBlocked` 标志，添加系统消息列出触发规则。`CriticRepairStep` 可从中提取信息进行修复。

## Reflexion + Self-Refine（NeurIPS 2023 + Madaan et al. 2023）

两个互补的自省层，在管线组 5（AlwaysRun CriticRepair 之后）执行：

### Self-Refine（单轮内迭代改进）

| 组件 | 文件 | 说明 |
|------|------|------|
| `SelfCritiqueGenerator` | `src/LTAI.Agent/Learning/SelfCritiqueGenerator.cs` | LLM 自批评：按 Completeness/Hallucination/Clarity/Verbosity/ToolUsage 五维度输出 critique |
| `SelfRefineStep` | `src/LTAI.Agent/Pipeline/Steps/SelfRefineStep.cs` | 管线步骤，调用 Generator → 若存在严重问题 → 调用 LLM 精炼 → 替换响应。默认 2 轮迭代 |

管线位置：`CriticRepair(alwaysRun) → SelfRefine → SelfReflection → Retrospective`

### Reflexion（跨会话经验记忆）

| 组件 | 文件 | 说明 |
|------|------|------|
| `ReflectionGenerator` | `src/LTAI.Agent/Learning/ReflectionGenerator.cs` | LLM 生成结构化的因果/纠正/预防三部反思 |
| `ReflectionStore` | `src/LTAI.Agent/Memory/ReflectionStore.cs` | PalaceStore 封装，向量+关键词双路检索 |
| `SelfReflectionStep` | `src/LTAI.Agent/Pipeline/Steps/SelfReflectionStep.cs` | 管线阻断时生成反思并持久化 |
| `ReflectionAugmentedStep` | `src/LTAI.Agent/Pipeline/Steps/ReflectionAugmentedStep.cs` | 预生成管线步骤，检索相关反思注入 system message |

反思格式：
```
## Causal Reflection
我失败是因为调用了 ReadFileContent 在文件夹而非文件上
## Corrective Strategy
下次应先调用 Glob 确认文件存在再读
## Preventive Guideline
所有文件操作前应先检查路径合法性
```

## ReWOO（Reasoning WithOut Observation, arXiv 2305.14229）

`ReWOOPlanningChatClient`（`src/LTAI.AI/ReWOOPlanningChatClient.cs`）将标准 ReAct 模式压缩为 2 次 LLM 调用：

1. **Planner**（L2 模型）：生成 `#E[N] ToolName(arg=val)` 格式的计划
2. **Worker**：机械执行所有工具，无 LLM 调用，占位符替换为观测结果
3. **Solver**（L3 模型）：一份完整上下文 + 所有观测 → 最终答案

通过 `LTAI_REWOO_ENABLED` 环境变量启用（默认 false）。`ReWOOPlanningChatClient` 继承 `DelegatingChatClient`，作为 IChatClient 中间件在 `ServiceCollectionExtensions.cs` 以 keyed service `"rewoo"` 注册。

**集成点：** MoA proposer 在 ReWOO 启用时自动用 ReWOO 包裹各自 client，实现 proposer 内嵌 planner→worker→solver：

```
MoA Layer 0: proposer[0]=ReWOO(l2) → proposer[1]=ReWOO(siliconflow) → proposer[2]=ReWOO(openrouter)
```
通过 `EnvironmentConfig.MoaDiversityMinProviders`（默认 2）保证 proposer 来源多样。

## DFSDTool（ToolLLM 深度优先决策树, arXiv 2305.14229）

`DFSDToolExecutor`（`src/LTAI.Agent/Execution/DFSDToolExecutor.cs`）实现深度优先搜索的决策树多工具执行器：

| 参数 | 默认值 | 环境变量 |
|------|--------|----------|
| `maxDepth` | 5 | `LTAI_DFSD_MAX_DEPTH` |
| `maxNodes` | 20 | `LTAI_DFSD_MAX_NODES` |

算法：
1. `ThinkAndActAsync` — LLM 决定下一步：`TOOL: Name(arg)` / `FINAL: answer` / `ABANDON`
2. `ExecuteActionAsync` — 通过 `IToolRegistry.InvokeToolAsync` 实际调用工具
3. 失败时回溯（DFS 栈回溯）
4. 重复直到 `maxDepth` / `maxNodes` 或找到最终答案

注册为 `IToolRegistry` 即可被 pipeline 使用。

## SWE-agent CodeRepairAci（Yang et al. 2024）

`CodeRepairAci`（`src/LTAI.Agent/Tools/CodeRepairAci.cs`）提供 SWE-agent 风格的 curated 代码修复动作空间：

```
view_file(path, start?, end?)   — 安全读取文件，自动行号引用
search_symbol(query)             — 使用 CgGraph 语义搜索
edit_lines(path, start, end, replacement) — 行号精确编辑
run_tests(filter?)               — 测试执行 + 结果解析
submit()                          — 标记修复完成
```

通过 `SafeShellTool` 包装 shell 执行，结果记录通过 `ToolRegistry` 的 BM25+向量 RRF 检索机制暴露给 agent。

## RepoCoder GenerationOrderStep（RepoCoder 启发）

`GenerationOrderStep`（`src/LTAI.Agent/Pipeline/Steps/GenerationOrderStep.cs`）在代码生成前执行拓扑排序，注入依赖顺序计划：

1. 使用 `ReachIndex.QueryImpact(symId, depth: 3)` 获取前向/反向可达符号
2. 构建依赖图后拓扑排序（基类 → 派生类，接口 → 实现）
3. 输出到 system message 供 LLM 参考

注入 `CgGraph`（通过 `CgGraph` 新增 `ResolveSymbolIdsAsync` 方法）做语义名称→ID 解析，替代原始哈希映射。

## ToolEvalStep（ToolLLM ToolEval 启发）

`ToolEvalStep`（`src/LTAI.Agent/Pipeline/Steps/ToolEvalStep.cs`）在管线并行组 4 运行，评估工具使用质量的 4 维度：

| 维度 | 权重 | 说明 |
|------|------|------|
| PassRate | 1.0 | 工具调用成功率 |
| ChainCompleteness | 0.8 | 不同类型工具数量 vs 总调用数的比例 |
| ArgumentQuality | 0.6 | 参数非空/有意义的比例 |
| Efficiency | 0.6 | 同一工具过度调用惩罚 |

未通过时设置 `QualityGateBlocked`，添加评估报告系统消息。通过阈值由 `LTAI_TOOL_EVAL_PASS_THRESHOLD` 控制（默认 0.65）。

## 更新后的 Pipeline 架构

### 执行顺序（加粗为新增）

```
Pre:  LoraAdapter → MemoryCaching(Restore) → ReflectionAugmented → RagContext
      → ProgressGuard → ProactiveSuggest → SafetyCheck → Router → ToolExecution
Post: DeltaAnchor → MemoryCaching(Save) → Compaction → DiscoursePlanning
      → Parallel{GrammarCheck, AntiPatternCheck, QualityGate, DoDCheck, ThinkingTag, **AbstentionCheck, ToolEval**}
      → CriticRepair → **SelfRefine → SelfReflection** → Retrospective
```

### 阻断链（更新）

| 标志 | 来源步骤 | 效果 |
|------|---------|------|
| `SafetyBlocked` | SafetyCheckStep | 安全拦截 |
| `GrammarCheckBlocked` | GrammarCheckStep | 语法错误，自动重试 |
| `AntiPatternBlocked` | AntiPatternCheckStep | 反模式注入修复指引 |
| `QualityGateBlocked` | QualityGateStep / ToolEvalStep | 质量门禁 |
| `DoDBlocked` | DoDCheckStep | DoD 检查失败 |
| `AbstentionBlocked` | AbstentionCheckStep | CONVOLVE 停止规则触发 |

## 环境变量（新增）

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `LTAI_REWOO_ENABLED` | false | 启用 ReWOO 规划模式 |
| `LTAI_SELF_REFINE_MAX_ITER` | 2 | SelfRefineStep 最大迭代轮数 |
| `LTAI_DFSD_MAX_DEPTH` | 5 | DFSDToolExecutor 最大搜索深度 |
| `LTAI_DFSD_MAX_NODES` | 20 | DFSDToolExecutor 最大节点数 |
| `LTAI_REFLECTION_TOP_K` | 3 | ReflectionStore 检索返回条数 |
| `LTAI_TOOL_EVAL_PASS_THRESHOLD` | 0.65 | ToolEvalStep 通过阈值 |
| `LTAI_MOA_DIVERSITY_MIN_PROVIDERS` | 2 | MoA proposer 最小来源 provider 数 |
