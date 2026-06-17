# UI 审查改进路线图

## P0 — 安全与崩溃修复（立即处理）

| # | 任务 | 影响 | 涉及 | 状态 |
|---|------|------|------|------|
| 1 | WebChatRenderer async void → ValueTask 接口，消除 fire-and-forget 进程崩溃 | 进程崩溃 | Web + Core + Agent | ✅ |
| 2 | Desktop ChatView CTS 竞态 (TOCTOU `IsCancellationRequested` + `Dispose`) | ObjectDisposedException | Desktop | ✅ |
| 3 | Web RateLimitMiddleware ConcurrentDictionary 竞态 (`_windows[key]` 抛 KeyNotFoundException) | 服务异常 | Web | ✅ |
| 4 | Web HMAC 加入 timestamp + nonce 防重放 | 安全漏洞 | Web | ✅ |

## P1 — 测试覆盖（高优先级）

| # | 任务 | 当前 | 目标 |
|---|------|------|------|
| 5 | Web 测试从测试桩迁移到 `WebApplicationFactory<Program>` | 12 个假测试 | 30+ 真实集成测试 |
| 6 | TUI ChatWindow 测试（流式/快捷键/命令） | <1% | 40% |
| 7 | Desktop ChatView 流式渲染测试 | 0% | 30% |
| 8 | Desktop TextPadView 文件/构建/Git 测试 | 0% | 25% |

## P2 — 架构改进

| # | 任务 | 原因 |
|---|------|------|
| 9 | TUI GetGitBranch 异步化 — WaitForExit(2000) 阻塞 UI 线程 2s | UX 卡顿 |
| 10 | TUI 消除 8 处空 catch | 调试困难 |
| 11 | Desktop ChatView.SendAsync 从 731 行拆分 | SRP 违反 |
| 12 | Web 审计端点添加 CancellationToken 传播 | 资源泄漏 |
| 13 | 三端统一 ILogger\<T\> 模式，消除 Debug.WriteLine | 可观测性 |
