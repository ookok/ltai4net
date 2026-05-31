# 2026-05-31 Session Persistence Improvements

## Goal
Connect SessionManager → ChatView and add SessionStatsPanel to MainWindow sidebar so users can save/load/switch sessions from the Desktop UI.

## Plan

### Steps
1. **SessionManager.cs** — add SaveSession() overload (no params, saves to _currentSession)
2. **ChatView.cs** — add _sessionManager field, constructor param, save after each response, /new calls NewSession(), expose LoadSession(string) + SessionManager property  
3. **MainWindow.cs** — create shared SessionManager, add SessionStatsPanel to sidebar, wire SessionSelected/NewSessionClicked to ChatView, refresh stats via timer

### Key Decisions
- SessionManager 在 MainWindow 中创建并注入 ChatView（而非各自独立），确保单例共享
- SessionStatsPanel 使用计时器轮询刷新而非事件通知，避免 ChatView/SessionManager 之间增加事件耦合
- NuGet 不变更 — 三个文件同在 LTAI.Desktop 项目，共享现有引用

### Files touched
- src/LTAI.Desktop/SessionManager.cs
- src/LTAI.Desktop/ChatView.cs
- src/LTAI.Desktop/MainWindow.cs

## Verification
- [ ] 构建通过：`dotnet build src/LTAI.Desktop`
- [ ] 现有测试通过：`dotnet test tests/LTAI.Tests`
- [ ] 手动验证：启动桌面端，新建会话、发送消息、关闭重开后会话列表显示历史
