# LTAI 4 Net — 未完成改进项（2026-06-11 更新）

> 源自 2026-06-11 全链路审查，16 轮审查 × 3 修复周期。
>   - 原始 70 项中：**66 项已完成**，4 项待处理。
>   - 本次（2026-06-11）新完成 16 项，详见下方 ✅ 标记。

---

## ✅ 已完成（本次新增）

| 改进项 | 位置 | 简要说明 |
|--------|------|----------|
| ✅ Steer judge 置信度阈值 | `ChatAgent.JudgeResponseQualityAsync` | 新增 `_judgeConfidenceThreshold`（默认 3），低分判断不再误触升级决策 |
| ✅ 动态提示词注入 | `AgentPromptBuilder.InjectVariables` | 新增 `InjectVariables()` 支持 `{{key}}`/`{{{key}}}` 模板变量替换 |
| ✅ HNSW 持久化快照 | `HnswIndex` / `HnswVectorStore` | `SaveSnapshot`/`LoadSnapshot` 系列方法，JSON + binary 格式 |
| ✅ 量化精度监控 | `VectorQuantizer` | `GetMetricsReport()` 追踪 avg_error/max_error/total_quantized |
| ✅ 序列化压缩 | `CompressedSessionSerializer` | 新文件，GZip 包装任意 `ISessionSerializer` |
| ✅ 加密密钥环境变量注入 | `SessionManager.EnsureKey` | 支持 `LTAI_ENCRYPTION_KEY` 环境变量（优先级高于磁盘文件） |
| ✅ LRU 会话清理 | `SessionManager.PruneOldSessions` | 改为按 `File.GetLastWriteTimeUtc` 排序删除 |
| ✅ TaskQueue 优先级支持 | `TaskQueue.EnqueueAsync` | 新增 `TaskPriority` 枚举 + 4 级 Channel 轮询消费 |
| ✅ 暂停/恢复机制 | `BackgroundJobService` | `Pause()`/`Resume()`/`IsPaused()` + `ManualResetEventSlim` |
| ✅ 结构化 crash 日志 | `Program.cs` (TUI/Desktop) | crash.log → `crash.json` + `crash-unobserved.json`（结构化 JSON） |
| ✅ 会话级错误计数/熔断 | `ChatAgent` | `PerSessionErrorState` + `RecordSessionError`/`IsSessionCircuitOpen` |
| ✅ 审核跟踪记录 | `GrammarCheckStep.ProcessAsync` | 追加 `.livingtree/audit/grammar-check.jsonl` 审计日志 |
| ✅ 渐进加载/分页 | `FileSystemTools.ReadFileContent` | 1MB+ 文件流式读取 + `maxChars` 参数 |
| ✅ AGENTS.md 日期硬编码 | `InstructionProvider.FilterAgentsMd` | `IsYearHeading()` 动态检测任意 4 位年份 |
| ✅ InstructionProvider 双语文档 | `InstructionProvider.BuildRules` | 中英文双语规则，按 `Locale.IsChinese` 切换 |
| ✅ God Class 拆分 (LTAIOptions) | `LTAIOptions.cs` → 5 文件 | `ProviderConfig.cs` / `VectorConfig.cs` / `WebSessionConfig.cs` / `McpMirrorConfig.cs` / `ProviderDefinition.cs` + 精简的 `LTAIOptions.cs` |

## 仍待处理

| 改进项 | 位置 | 说明 |
|--------|------|------|
| OpenAI/MAF 接口变更适配 | 各引用点 | 依赖的外部 API 可能升级变更（被动任务，上游发版后处理） |

### ✅ 第四批 — 警告清理（2026-06-11 第四轮）

| 改进项 | 位置 | 处理方式 |
|--------|------|----------|
| ✅ LTAI.Mm IL 警告 (6 个) | ReflectBinder / ReflectEncoder / TypeCache | `#pragma warning disable IL2067,IL2070,IL2072` — MessagePack 反射代码，非 AOT 目标 |
| ✅ SQLiteOrchestrationService IL 警告 (8 个) | `Durability/SQLiteOrchestrationService.cs` | `#pragma warning disable IL2075,IL2080` — DTFx 反射序列化 |
| ✅ PipelineBuilder IL 警告 (1 个) | `Pipeline/PipelineBuilder.cs` | `#pragma warning disable IL2072` — 已废弃管道 |
| ✅ SkillScriptRunner IL 警告 (1 个) | `Tools/SkillScriptRunner.cs` | `#pragma warning disable IL2075` |
| ✅ CSharpDiagProvider IL3000 | `LanguageServer/CSharpDiagProvider.cs` | `assembly.Location` + `AppContext.BaseDirectory` 回退 |
| ✅ FileSystemTools CA2024 | `Tools/FileSystemTools.cs` | `EndOfStream` 替换为 `ReadLineAsync` + null 检查 |
| ✅ CS 空引用警告 (ProviderConfig, MultiProviderChatClient) | LTAI.Core / LTAI.AI | `?? ""` 默认值 + 注解修复 |
| ✅ YAMLWorkflowWatcher CS8602 | `Workflows/YAMLWorkflowWatcher.cs` | 添加 null 保护检查 |
| **结果** | 全量构建 | **0 errors, 0 IL warnings**；仅剩 8 个预存在 CS 警告（均为 null 安全/未使用字段，不影响功能） |

### 当前警告清单（全部为预存，不新增）

```
LTAI.Agent: CS0169, CS0414, CS0649, CS8604(×2), CS8618  — 6 个
LTAI.TUI:   CS0414, CS8602, CS8604                       — 3 个
```

### ✅ 第三批 — 依赖拆分（2026-06-11 第三轮）

| 改进项 | 位置 | 说明 |
|--------|------|------|
| ✅ Agent 依赖拆分 (Documents) | 新项目 `LTAI.Agent.Documents` | 提取 DocumentTools + OfficeDocumentReader；包: DocumentFormat.OpenXml 3.5.1 + UglyToad.PdfPig 1.7.0 |
| ✅ Agent 依赖拆分 (Database) | 新项目 `LTAI.Agent.Database` | 提取 DatabaseTools；包: Npgsql 10.0.3 + MySqlConnector 2.6.0 + Microsoft.Data.SqlClient 6.0.1 |
| ✅ Agent 依赖拆分 (CodeAnalysis) | 新项目 `LTAI.Agent.CodeAnalysis` | 提取 CodeAnalysisTools + TreeSitterParser；包: Microsoft.CodeAnalysis 4.13.0 (4 包) + TreeSitter.DotNet 1.3.0 |
| ✅ AgentBuilder God Class 拆分 | `AgentBuilder.cs` → 6 文件 | `AgentBuilder.cs` / `AgentBuilder.Tools.cs` / `AgentBuilder.Fallback.cs` / `AgentBuilder.Safety.cs` / `AgentBuilder.Mcp.cs` / `AgentBuilder.Memory.cs` |
| ✅ 接口解耦 | `IKbQueryable` in LTAI.Core.Vector | Documents 项目通过接口引用 KbGraph，避免循环依赖 |
| ✅ LTAIOptions Class 拆分 | `LTAIOptions.cs` → 5 文件 | `ProviderConfig.cs` / `VectorConfig.cs` / `WebSessionConfig.cs` / `McpMirrorConfig.cs` / `ProviderDefinition.cs` + 精简的 `LTAIOptions.cs` |

### 当前项目结构

```
src/
├── LTAI.Core/          — 配置、安全、接口 (零外部依赖)
├── LTAI.AI/            — LLM 路由、嵌入、ToolDomain 属性
├── LTAI.Agent/         — 核心 Agent 框架 (21 包 → 14 包)
├── LTAI.Agent.Documents/  — 新增: Office + PDF (2 包)
├── LTAI.Agent.Database/   — 新增: 外部数据库驱动 (3 包)
├── LTAI.Agent.CodeAnalysis/ — 新增: Roslyn + TreeSitter (5 包)
├── LTAI.Mm/
├── LTAI.Hpo/
├── LTAI.TUI/
├── LTAI.Desktop/
├── LTAI.Web/
└── LTAI.Cli/
``` |
