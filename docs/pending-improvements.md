# LTAI 4 Net — 未完成改进项

> 源自 2026-06-11 全链路审查，16 轮审查 × 3 修复周期后遗留的 P2 级改进。
> 50 项已完成，以下 20 项待处理。

---

## 用户意图分类与语义理解

| 改进项 | 位置 | 说明 |
|--------|------|------|
| Steer judge 置信度阈值 | `ChatAgent.JudgeResponseQualityAsync` | 当前无置信度过滤，低质量判断直接覆盖升级决策 |
| 动态提示词注入（基于用户历史） | `AgentPromptBuilder` | 提示词固定不变，无法根据对话历史调整策略 |

## 知识/代码图谱存储召回

| 改进项 | 位置 | 说明 |
|--------|------|------|
| HNSW 持久化快照 | `HnswIndex` / `HnswVectorStore` | 重启后全量重建 O(n)，大图冷启动慢 |
| 量化精度监控 | `VectorQuantizer` | TurboQuant 4-bit 量化精度损失无监控指标 |

## Session 处理

| 改进项 | 位置 | 说明 |
|--------|------|------|
| 序列化压缩 | `JsonSessionSerializer` / `MmSessionSerializer` | 大型会话文件无压缩（可 GZip/Deflate） |
| 加密密钥环境变量注入 | `SessionManager.EnsureKey` | 密钥仅存在磁盘文件，进程可读（建议支持 env var） |
| LRU 会话清理 | `SessionManager.PruneOldSessions` | 按文件名称排序删除，应改为按最后活动时间 |

## 提示词工程

| 改进项 | 位置 | 说明 |
|--------|------|------|
| Router prompt 模板占位符统一 | `agents/*.prompt.md` | 有的用 `{{task}}` 有的用 `{{{key}}}` |
| AGENTS.md 过滤日期硬编码 | `InstructionProvider.FilterAgentsMd` | `"# 2026-"` 硬编码，跨年失效 |
| InstructionProvider 双语文档 | `InstructionProvider.BuildRules` | 中英文规则在 C# 代码中硬编码，未使用 Locale |

## 长时任务处理

| 改进项 | 位置 | 说明 |
|--------|------|------|
| TaskQueue 优先级支持 | `TaskQueue.EnqueueAsync` | Channel 为无界无优先级，无法优先处理重要任务 |
| 暂停/恢复机制 | `BackgroundJobService` | 运行中的后台任务无法暂停/恢复 |

## 错误复盘与纠偏

| 改进项 | 位置 | 说明 |
|--------|------|------|
| 结构化 crash 日志 | `Program.cs UnhandledException` | TUI/Desktop 的 crash.log 为纯文本，非结构化 |
| 会话级错误计数/熔断 | `ChatAgent` | 无 per-session 级错误计数或熔断机制 |

## 结果验收与审计

| 改进项 | 位置 | 说明 |
|--------|------|------|
| 审核跟踪记录 | `GrammarCheckStep` | 无验证历史可追溯（谁、何时、验证了什么） |

## 超长输入与超大文档

| 改进项 | 位置 | 说明 |
|--------|------|------|
| 渐进加载/分页 | `FileSystemTools.ReadFileContent` | 超大文件一次性读取到内存，无流式加载 |

## 配置与项目结构

| 改进项 | 位置 | 说明 |
|--------|------|------|
| God Class 拆分 | `AgentBuilder` (555行), `LTAIOptions` (500行) | 需要大规模重构 |
| Agent 依赖拆分 | `LTAI.Agent.csproj` (20+ PackageReference) | 文档处理/数据库/代码分析拆分为独立项目 |

## 代码质量

| 改进项 | 位置 | 说明 |
|--------|------|------|
| pre-existing IL 警告清理 | 各项目 csproj | ~38 个 NativeAOT IL 相关警告 |
| OpenAI/MAF 接口变更适配 | 各引用点 | 依赖的外部 API 可能升级变更 |
