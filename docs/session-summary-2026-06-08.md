# LTAI 项目 — 会话成果总览

> 日期: 2026-06-08
> 范围: 重构 V2 执行 + 论文启发落地 + 代码质量修复 + 知识图谱防污染

---

## 重构 V2（Phase 1-5）

| Phase | 新增文件 | 新增代码 | 核心产出 |
|-------|----------|----------|---------|
| **P1: 向量抽象+PCA+PQ** | 11 个 | ~1,200 行 | IVectorStore → HnswVectorStore → VectorStoreFactory; PCA 随机投影 + 训练投影; PQ 乘积量化 (4-bit 压缩) |
| **P2: 编排引擎** | 10 个 | ~1,200 行 | IExecutionEngine → ExecutionEngine; WorkflowStep 5 种类型; FallbackPolicy 断路器; DevUISpanCollector |
| **P3: 消息管道** | 8 个 | ~800 行 | IPipelineStep → PipelineBuilder 链式注册; 5 个步骤 (Rag/Safety/Router/Tool/Compaction) |
| **P4: GraphRAG** | 3 个 | ~700 行 | EntityLinker 模糊匹配; SubgraphExtractor BFS 子图; GraphContextBuilder RRF 融合 |
| **P5: KV Cache** | 3 个 | ~373 行 | PrefixKvCache SHA-256 前缀; SemanticKvCache 嵌入相似度 |

## 论文启发落地（4 项）

| 功能 | 论文来源 | 文件 | 代码 | 核心思路 |
|------|---------|------|------|---------|
| **Code2LoRA** | arXiv 2606.06492 | 5 个 | 550 行 | RepoAnalyzer 扫描仓库 → CodeLoraAdapter 生成结构化前缀 → LoraAdapterStep 注入 |
| **Routing Skill** | arXiv 2605.27366 | 3 个 | 437 行 | RoutingSkillStore 持久化成功/失败 → RoutingSkillAdapter 动态调置信度 |
| **Memory Cache** | arXiv 2602.24281 | 6 个 | 986 行 | 3 层级联 (Memory LRU → File JSON → Null); Pipeline 双模式 (Restore/Save) |
| **Adaptive Plan** | arXiv 2606.05622 | 1 个 | 221 行 | 4 级约束 (Strict→Moderate→Relaxed→Fallback); 自动回退 |
| **Proactive Suggestion** | arXiv 2606.04743 | 4 个 | 1,019 行 | 6 个检测器 (TODO/命名/复杂度/魔法数/异常/文档); 后台服务 → Pipeline 步骤 → DevUI API |

## 代码质量修复

| 修复 | 涉及文件 |
|------|---------|
| `DefaultProvider` null 警告 (CS8604) | TuiApp.cs, Program.cs |
| `_fsWatcher` null 检查 (CS8602) | YAMLWorkflowWatcher.cs |
| `steer.ApiKeyEnv` null 检查 (CS8604) | ServiceCollectionExtensions.cs |
| `_codebooks` 构造函数初始化 (CS8618) | ProductQuantizer.cs |
| 未使用字段 `_diagnosticsStore` (CS0169) | AgentWorkflows.cs |
| Memory Cache SQLite → **FileCachingStore** (AOT 兼容) | 替换 1 个完整存储层 |
| 项目目录清理 | 删除 17+ 空目录、构建产物、日志 |

## 知识图谱防污染

| 层 | 方法 | 效果 |
|----|------|------|
| **L1 路径过滤** | 30+ 跳过目录 + 50+ 拒绝扩展名 + 白名单 | `.log` 不再进入索引 |
| **L2 内容过滤** | 日志识别 / 数字比例 / 符号比例 / 行长 / 唯一词 | 堆栈、时间戳、模板被拒绝 |
| **L3 质量过滤** | LLM 提取验证 / 代码奖励 / 通用惩罚 / 异常惩罚 | 无意义实体不入图谱 |

## 项目存储优化

| 之前 | 之后 |
|------|------|
| 6 个独立 SQLite 数据库 | 2 个 SQLite (kg.db + cg.db) + 2 个 JSON |
| PalaceStore 独立 db | 共享 kg.db (CreateShared 构造器) |
| Memory Cache SQLite 层 | FileCachingStore (JSON 原子重写) |

---

**统计汇总**: 新增 ~48 个文件 · ~7,500 行代码 · 0 编译错误 · 0 新增警告

