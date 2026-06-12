# Batch 1: 多图记忆架构 (MAGMA-inspired)

**来源论文**: [MAGMA: A Multi-Graph based Agentic Memory Architecture for AI Agents](https://arxiv.org/abs/2601.03236), ACL 2026
**优先级**: P0 | **工作量**: 15-20 人天 | **目标模块**: `src/LTAI.Agent/Memory/`

## 现状分析与痛点

当前 LTAI 记忆系统的核心问题：

1. **单维度存储**: `KgStore` (kg.db/cg.db) 用单一 SQLite FTS5 + 384d 向量存储，语义/时间/因果/实体全混在 cosine 相似度里。`cos(A,B) > threshold` 无法区分"相似事件"和"原因事件"。
2. **盲检索**: `MemoryExtractor` 对所有 query 都用同一种语义检索，无法区分"什么时候"vs"为什么"vs"谁"。
3. **同步写入**: `MemoryCachingStep` 在关键路径上做记忆写入，影响响应延迟。
4. **无因果推理**: 能回答"发生了什么"，但回答不了"为什么"。Adversarial 类查询（语义相似但因果无关的 distractor）会严重误导检索。
5. **Memory Palace 7 层**: 结构有层次但全是语义相似度驱动，缺少显式关系建模。

## 改进目标

- 将单一记忆库拆分为四张正交关系图
- 实现意图感知的自适应图检索 (QueryIntent: Why / When / Entity / What)
- 快慢双流解耦读写，不阻塞用户响应
- 在 LoCoMo-class 长对话场景中 token 节省 30%+ 同时提升回答质量

## 论文核心设计

### 四张正交关系图

```
Graph = (Nodes, Edges), 每个 Node = {Content, Timestamp, Vector<384d>, Attributes}

TemporalGraph:   n_{t-1} → n_t  (不可变时间链，提供时序基线)
CausalGraph:     cause → effect  (条件得分 > δ, LLM 异步推理注入)
SemanticGraph:   undirected, cos(v_i, v_j) > θ_sim (现有逻辑迁移)
EntityGraph:     event → entity  (同一对象跨时段识别)
```

四张图共享节点身份，边按维度独立访问。向量库保留作粗筛 anchor 入口。

### Intent-Aware Adaptive Traversal

```
TransferScore(n_j | n_i, q) = exp(λ₁·φ(edgeType, queryIntent) + λ₂·sim(vec_n_j, vec_q))

where φ(r, T_q) = w_{T_q}^T · 1_r  (intent 给对应边类型加权)
  - Why   → causal 边 3-5, temporal 1.0
  - Entity → entity 边 2.5-6
  - When   → temporal 边 4-6
```

RRF 融合 vec + keyword + time 三路信号定位入口锚点，然后 beam search (width=3, maxDepth=5) 做 policy-guided traversal。
Salience-based token budgeting: 低分节点压缩为 "...N 个中间事件..."，高分节点保留全文。

### 双流写入

```
Fast Path (同步, 不调 LLM):
  - 事件分割 + 编码向量 (all-MiniLM-L6-v2, <50ms)
  - 追加时间骨干边 n_{t-1} → n_t
  - 写入向量库 + 推进 SlowPath queue

Slow Path (异步 worker, 调 LLM):
  - 从 queue 取节点，拉 2-hop 邻域
  - LLM(BudgetL2) 推理因果边 + 实体边
  - 写回图
```

对应认知科学 CLS 理论：海马(快) + 新皮层(慢) 互补分工。

### 关键数据

| 方法 | Multi-Hop | Temporal | Adversarial | Overall | Latency |
|------|-----------|----------|-------------|---------|---------|
| A-MEM | 0.495 | 0.474 | 0.616 | 0.580 | 2.26s |
| MAGMA | **0.528** | **0.650** | **0.742** | **0.700** | **1.47s** |

Adversarial 提升 +12.6 百分点，时延快 40%。消融中 Causal Graph 贡献最大 (-0.056 when removed)。

## LTAI 实施方案

### 新增文件

```
src/LTAI.Agent/Memory/
├── MultiGraphStore.cs          # 多图门面，统一 CRUD + 事务
├── TemporalGraph.cs            # 时间链子图
├── CausalGraph.cs              # 因果边子图
├── SemanticGraph.cs            # 语义向量子图 (现有逻辑迁移)
├── EntityGraph.cs              # 实体关联子图
├── IntentRouter.cs             # 意图分类 (Why/When/Entity/What)
├── AdaptiveBeamTraverser.cs    # intent-weighted beam search
├── MemoryConsolidationWorker.cs # BackgroundService, Slow Path 异步巩固
└── SalienceBudgetCompressor.cs  # token budget 压缩
```

### 修改现有文件

| 文件 | 变更 |
|------|------|
| `KgStore.cs` | 标记为 [Obsolete]，保留向后兼容适配层 |
| `MemoryExtractor.cs` | 增加 `RetrieveWithIntent()` 重载 |
| `MemoryCachingStep.cs` | Save: 原逻辑变 Fast Path；新增 Slow Path enqueue |
| `RagContextStep.cs` | 注入路径改用 intent-aware retrieval |
| `KbGraph.cs` | 内部调用从 KgStore 切换到 MultiGraphStore |

### 扩展方法

```csharp
// src/LTAI.Agent/ServiceCollectionExtensions.cs
services.AddLTAIMemory(config =>
{
    config.UseMultiGraph(schema =>
    {
        schema.WithTemporalGraph();
        schema.WithCausalGraph(llmTriggerThreshold: 0.7);
        schema.WithSemanticGraph(similarityThreshold: 0.6);
        schema.WithEntityGraph();
        schema.WithVectorIndex(dimension: 384);
    });
    config.ConsolidationWorker(interval: TimeSpan.FromSeconds(30), maxBatchSize: 50);
});
```

### DB Schema: 从单表到多表

现有 `kg.db`:
```sql
CREATE TABLE memory_nodes (id TEXT, content TEXT, embedding BLOB, ...);
CREATE VIRTUAL TABLE memory_fts USING fts5(content);
```

新 `kg.db`:
```sql
-- 共享节点表
CREATE TABLE nodes (id TEXT PRIMARY KEY, session_id TEXT, content TEXT,
    embedding BLOB, created_at INTEGER, attributes TEXT);

-- 四个独立边表
CREATE TABLE temporal_edges (from_id TEXT, to_id TEXT, seq INTEGER);
CREATE TABLE causal_edges (from_id TEXT, to_id TEXT, score REAL, llm_label TEXT);
CREATE TABLE semantic_edges (from_id TEXT, to_id TEXT, similarity REAL);
CREATE TABLE entity_edges (node_id TEXT, entity_id TEXT, entity_type TEXT);

-- SlowPath 队列
CREATE TABLE consolidation_queue (id TEXT, node_id TEXT, status TEXT, created_at INTEGER);
```

### 验收标准

1. [ ] `MemoryExtractorTests` 增加 intent-aware 测试用例 (4 种 QueryIntent 各 10 条)
2. [ ] LoCoMo-class 长对话手工评测: Judge score 基线 A-MEM style → 目标 +15%
3. [ ] 单次 memory retrieval token 开销 ≤ 当前 70%
4. [ ] Fast Path 写入延迟 ≤ 50ms (P99)
5. [ ] Slow Path 不阻塞主循环, 30s 内完成因果边注入
6. [ ] 现有 Memory Palace L0-L6 抽象接口不破裂

## 关键风险与缓解

| 风险 | 概率 | 缓解 |
|------|------|------|
| 多图存储磁盘占用翻倍 | 中 | SQLite 共享 DB + 按图分表, 预估 +40% |
| Slow Path LLM 抽取错误边 | 中 | 保守阈值 δ=0.7, 错误边仅影响检索不阻塞主流程 |
| 向量索引与图索引一致性问题 | 低 | 写入事务包裹, 节点 UUID 统一 |
| 现有 Memory Palace 接口破裂 | 低 | 保留 L0-L6 抽象, 底层存储替换 |

## 参考

- MAGMA paper: https://arxiv.org/abs/2601.03236
- MAGMA code: https://github.com/FredJiang0324/MAGMA
- LoCoMo benchmark: ACL 2024
- 认知科学 CLS 理论: McClelland et al. 1995
