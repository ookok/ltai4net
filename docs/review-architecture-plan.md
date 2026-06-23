# LTAI 4 Net — 架构审查与改进计划

## 总览

14 个项目、22 个 Agent、80+ 工具、20 个 Context Provider、15 步 Pipeline、42 个内存文件、
50+ 配置类。复杂度约为必要水平的 3-5 倍。本计划分四阶段重构，目标是 2-4x 性能提升
+ 大幅降低复杂度。

---

## 一、关键问题

### 1. SQLite 数据库爆炸

多个独立 SQLite 数据库：`kg.db`、`knowledge_graph.db`、`memory.db`、`sessions/*`、
`CircuitBreakerStore.db`、HPO 数据库。每个都有独立的连接池、WAL 文件、内存页缓存。

**方案**：统一为单数据库 `ltai.db`，表前缀分区：`palace_*`、`cg_*`、`kg_*`、`session_*`、
`cb_*`、`hpo_*`。

### 2. 过度缓存

至少 8 个独立内存缓存，各有淘汰策略、锁、内存预算。无法全局控制内存峰值。

**方案**：统一为 `LTAICache<TKey,TValue>` 核心缓存服务，全局内存预算 + 统一 LRU/TTL。

### 3. Pipeline 串行瓶颈

15 步 Pipeline 完全串行。`CompactionStep`（423 行）+ `GrammarCheckStep`（859 行）
消耗大量时间。许多步骤互不依赖，可并行。

**方案**：引入 DAG 调度 + `System.Threading.Channels`，无关步骤并行运行。

### 4. DI 注册爆炸

DI 注册近千行，80-100 个 `AddSingleton`/`AddScoped`。依赖图极其复杂。

**方案**：Source Generator 自动注册，或功能模块自注册。

### 5. Agent 过度细粒度

22 个 Agent 许多职责重叠，可合并：
- LTAI-Code + LTAI-Frontend + LTAI-API → **LTAI-Dev**
- LTAI-SQL + LTAI-Data → **LTAI-Data**
- LTAI-Review + LTAI-Test + LTAI-Debug → **LTAI-QA**
- LTAI-DevOps + LTAI-Security → **LTAI-Ops**
- LTAI-Chat + LTAI-Chat-Pro + LTAI-LLM → **LTAI-Chat**（带 Pro 开关）

**效果**：22 → 12，-45% 注册开销。

### 6. 过多的 IChatClient 包装层

调用链 10+ 层：ChatAgent → HarnessAgent → SubagentContextIsolation →
ToolFilteringChatClient → LlmLoggingChatClient → ProgressGuardChatClient →
ThinkingTagValidator → MultiProviderChatClient → MetricsChatClient →
SafeChatClient → ThinkingChatClient → ProviderClientManager → HTTP Client。

**方案**：合并为 3-4 层，降低序列化开销。

### 7. 7 层记忆宫殿过于复杂

PalaceStore（L0-L6）+ MemoryRefinery + ContextOffloader + MermaidStateTracker
+ ConsolidationService 等 10+ 类。

**方案**：简化为 3 层：L0 原始会话、L1 结构化记忆、L2 合成记忆。

### 8. 全局静态状态

`AgentBuilder.s_serviceProvider`、`AgentModeObserver` 静态属性、
`static readonly` 实例等导致测试困难、线程安全问题。

---

## 二、性能改进方案

### 1. 管道并行化

PipelineRunner 改为 DAG 调度器：
- 无关步骤并行执行
- 使用 `System.Threading.Channels` 实现步骤间通信
- 预期加速 2-4x

### 2. 延迟初始化 Provider

AIContextProvider 改为按需初始化，使用 `Lazy<Task<T>>`。

### 3. 减少序列化开销

GZip 改为 Brotli（更高压缩比）或 LZ4（更高速度）。

### 4. 嵌入流水线短路

ONNX 可用时直接返回，避免下游 API/GloVe/BM25 调用。

### 5. 统一 KV 缓存抽象

`IResponseCache` + `IEmbeddingCache` + `IKvCache` 三个标准接口。

---

## 三、通用性改进

### 1. 移除 MAF 强耦合

Core 定义 `IAgent`、`IChatService`、`IToolContext`，MAF 实现隔离到适配器项目。

### 2. Provider 插件化

`ILlmProvider` 接口 + JSON 配置 + 反射加载，无需重新编译。

### 3. 工具链热加载

`IAgentTool` 接口 + `tools/*.tool.json` 声明式注册 + 热重载。

### 4. Agent 定义可编程化

保留 YAML 声明，正文可选从 code-behind 生成。

---

## 四、四阶段路线图

### Phase 0 — 诊断工具（1 天）

建立性能基准：启动时间、Pipeline 步骤延迟、SQLite 池使用率、缓存命中率、Agent 热力图。

### Phase 1 — 核心简化（1-2 周）

| 步骤 | 内容 | 预期效果 |
|------|------|----------|
| 1.1 | 合并 SQLite 数据库 | -50% 数据库连接 |
| 1.2 | 统一缓存层 | -40% 内存占用 |
| 1.3 | 合并 Agent 22→12 | -45% 注册开销 |
| 1.4 | 7 层记忆→3 层 | -60% 记忆代码 |

### Phase 2 — 性能优化（2-3 周）

| 步骤 | 内容 | 预期加速 |
|------|------|----------|
| 2.1 | Pipeline DAG 并行化 | 2-4x |
| 2.2 | 减少 IChatClient 包装层 | 15-30% |
| 2.3 | 嵌入短路 | 20-50% |
| 2.4 | 延迟初始化 Provider | 30-50% 启动 |
| 2.5 | LZ4 序列化 | 2x 读写速度 |

### Phase 3 — 架构去耦合（2-3 周）

| 步骤 | 内容 |
|------|------|
| 3.1 | MAF 抽象层隔离 |
| 3.2 | Provider 插件化 |
| 3.3 | 工具接口标准化 |
| 3.4 | 消除全局静态状态 |

### Phase 4 — 长期演进

`System.Threading.Channels` 响应式管道、Source Generator DI、WASM 插件沙盒、
OpenTelemetry 第一公民化。

---

## 五、优先级矩阵

| 优先级 | 问题 | 严重程度 | 修复难度 | ROI |
|--------|------|----------|----------|-----|
| P0 | SQLite 数据库爆炸 | 🔴 性能 | 中 | 高 |
| P0 | Pipeline 串行瓶颈 | 🔴 延迟 | 中 | 最高 |
| P1 | 过度缓存 | 🟡 内存 | 低 | 高 |
| P1 | 22 个 Agent 过多 | 🟡 复杂度 | 低 | 高 |
| P1 | 10+ 层 IChatClient 包装 | 🟡 延迟 | 中 | 高 |
| P2 | MAF 强耦合 | 🟡 可维护性 | 高 | 中 |
| P2 | 全局静态状态 | 🟡 测试性 | 中 | 中 |
| P3 | Provider 非插件化 | 🟢 通用性 | 高 | 低 |
