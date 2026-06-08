# LTAI 4 Net 项目审计报告

日期: 2026-06-08
审计范围: 设计缺陷、功能未完成、Bug、性能、用户体验

---

## 总览

| 维度 | 评级 | 问题数 |
|------|------|--------|
| Bug 风险 | C- | 132 个严重问题 |
| 设计缺陷 | C | 16 个 god class，多处违反 SRP |
| 功能未完成 | D | 11 个测试文件被排除，11 处 NotSupportedException |
| 性能问题 | C | 39 处 Task.Run，18 处同步阻塞，11 处 new HttpClient |
| 用户体验 | C | 51 处调试残留，95 处空 catch 吞没错误 |

---

## P0 — 必须立即修复

### 1. 异步死锁 — 33 处同步阻塞

| 文件 | 行号 | 模式 |
|------|------|------|
| `src/LTAI.TUI/Services/SnippetCommandService.cs` | 34,57,93,104,108,116,123,133 | `.GetAwaiter().GetResult()` |
| `src/LTAI.TUI/Services/WorkflowCommandService.cs` | 91,106 | `.GetAwaiter().GetResult()` |
| `src/LTAI.TUI/Services/McpCommandService.cs` | 85 | `.GetAwaiter().GetResult()` |
| `src/LTAI.TUI/Services/ModelCommandService.cs` | 377 | `Task.Run().GetAwaiter().GetResult()` |
| `src/LTAI.TUI/GraphBrowserView.cs` | 105 | `.GetAwaiter().GetResult()` |
| `src/LTAI.TUI/ResponseStreamer.cs` | 203 | `.GetAwaiter().GetResult()` |
| `src/LTAI.Hpo/Samplers/TpeSampler.cs` | 32,108 | `.GetAwaiter().GetResult()` |
| `src/LTAI.Agent/Agents/AgentBuilder.cs` | 59 | `Task.Run().GetAwaiter().GetResult()` |
| `src/LTAI.Agent/Agents/AgentDefinitionLoader.cs` | 42 | `Task.Run().GetAwaiter().GetResult()` |

此外 `ChatAgent.cs`, `SQLiteTaskStore.cs`, `TaskQueue.cs`, `SubagentTools.cs` 中存在 `.Result`。

### 2. async void 方法 — 10 处

| 文件 | 方法 |
|------|------|
| `src/LTAI.Desktop/ChatView.cs` | `ShowCmdPicker`, `ShowSnippetList`, `TrySaveSnippet`, `TryUseSnippet`, `TryDeleteSnippet`, `TryRenameSnippet` |
| `src/LTAI.Desktop/MemoryView.cs` | `ShowStats` |
| `src/LTAI.Desktop/TextPadView.cs` | `RunGitCmd` |
| `src/LTAI.Accelerator/MainWindow.axaml.cs` | `OnStartClick`, `OnStopClick` |

---

## P1 — 高优先级

### 3. 空 catch 块 — 95 处

分布: LocalEmbedder(6), DebugToolbar(6), TextPadView(6), ProxyService(5), LspClient(5) 等 50 个文件。

### 4. 泛 catch(Exception) — 277 处

主要: DocumentTools, SystemTools, WebTools, ChatView, TuiApp 等 97 个文件。

### 5. 测试文件排除 — 11 个文件

- LTAI.Tests: CodeSearchRerankerTests, CoreTests, A2A/**
- LTAI.Desktop.Tests: 8 个测试文件全部排除，项目完全失效

### 6. NotSupportedException — 11 处

- YAMLWorkflowHost.cs (5), YAMLWorkflowRegistry.cs (5), DevUIView.cs (1)

### 7. 线程安全 — 静态可变状态

- SlashCommands.cs:27 — `public static string[] CascadeStack`
- SlashCommands.cs:129 — `static Dictionary<string, int>`
- LTAIOptions.cs:789-803 — `KnownKeys.All` 可写数组
- ChatLayout.cs:90 — `public static string? EditMode`

### 8. 脆弱的反射 — SQLiteOrchestrationService.cs:73-119

---

## P2 — 中优先级

### 9. God Class 拆分

| 文件 | 行数 | 职责 |
|------|------|------|
| TextPadView.cs | 1820 | 编辑器 + git + 高亮 + 搜索 |
| ChatView.cs | 1811 | 聊天 + snippets + session + diff |
| KgStore.cs | 1371 | SQLite + FTS + 向量搜索 + HNSW |
| LTAIOptions.cs | 1353 | 配置 + 用量 + 定价 |
| LocalEmbedder.cs | 1021 | ONNX + 下载 + GPU |
| ChatLayout.cs | 813 | 输入 + 渲染 + 历史 + 布局 |
| AgentBuilder.cs | 885 | 单一方法 750 行 |

### 10. 调试残留 — 51 处

- Debug.WriteLine: 29 处
- Console.WriteLine: 22 处

### 11. 静态 HttpClient — 7 处

- ChatView.cs, ConfigViewModel.cs, ChatMessageRenderer.cs
- LTAIOptions.cs, SystemTools.cs, FileDownloadTool.cs, CommandHelpers.cs

### 12. 25+ 文件缺少 ConfigureAwait(false)

---

## P3 — 低优先级

### 13. 硬编码 URL/路径
- mogoo.com.cn (4), C:\Program Files (3), C:\Windows (3), ip-api.com

### 14. 魔法数字
- 64000, 2000, 80, 20, 100_000, 11818, 6000/1000

### 15. HTTP 方法注入风险 - WebTools.cs:182

---

## 修复计划

### 第一批: P0 异步死锁 + async void
1. 重构 `ICommandService` 接口为 async
2. 修复 `SnippetCommandService` 等所有 `.GetAwaiter().GetResult()`
3. 转换 10 处 `async void` 为 `async Task`

### 第二批: P1 异常处理 + 测试
1. 95 处空 catch 添加 ILogger 日志
2. 277 处泛 catch 规范化为统一策略
3. 恢复 Desktop 测试项目

### 第三批: P2 性能 + 设计
1. God Class 拆分（ChatView, TextPadView, KgStore, LTAIOptions）
2. 调试残留清理
3. HttpClient 管理改进
4. 添加 IUIHost 抽象

### 第四批: P3 安全 + 配置
1. URL/路径配置化
2. 魔法数字提取
3. HTTP 方法验证
