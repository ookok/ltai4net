# TUI 布局重构计划

原则：简洁优美，沉浸式体验。

## 当前问题

1. **Header 信息过载** — 2 行塞满快捷键、模型名、工具调用次数
2. **消息边框过重** — 每条消息独立 Panel，4 种边框样式互不协调
3. **Footer 10 行臃肿** — 统计 + 输入 + 建议堆叠
4. **颜色系统无效** — ThemeService 只改背景色，前景全硬编码
5. **沉浸感缺失** — 到处是边框和数字，像 IDE 面板而非聊天

## Phase 1: 色彩系统统一（地基）

目标：所有颜色由 ThemeService 管理，深色/浅色各一套完整 palette。

```
Services/ThemeService.cs          — 定义完整 palette
Rendering/MessagePanelRenderer.cs — 颜色替换
Rendering/FooterRenderer.cs       — 颜色替换
Rendering/ChatRenderer.cs         — 颜色替换
DevUI/DevUIDashboardView.cs       — 颜色替换
ChatLayout.cs:174                 — 颜色替换
```

## Phase 2: 布局框架重构（骨架）

ChatLayout 从 3 区 → 2 区：

```
当前:                         改为:
┌─ Header (2行) ────┐        ┌─ Messages ──────────┐
├─ Messages ────────┤        │  (无外边框)          │
├─ Footer (10行) ───┤        ├─ Footer (4行) ──────┤
└───────────────────┘        │  输入区(3行)          │
                              │  状态条(1行)          │
                              └──────────────────────┘
```

- Header 完全移除，快捷键移至 /help / Ctrl+K
- 模型名 + token 数压缩为底部单行状态条
- 多视图导航提示从小 Header 改为底部 tab 条

## Phase 3: 无框气泡（视觉核心）

```
当前:                         改为:
┌─ 🧑 You ──────┐           ▎你好，请帮我
│ 你好，请帮我    │           写一个排序算法
└────────────────┘
                              ▎以下是用 Python 实现
┌─ 🤖 AI ────────┐           的快排算法...
│ 以下是用 Python │
│ 实现的快排...   │
└────────────────┘
```

- 每条消息不再包 Panel，改用 Markup + 左侧角色色条
- 消息间用空行而非边框线分割
- 角色色由色条颜色隐含，hover 时显示 label
- 推理过程折叠用 typography 而非边框

## Phase 4: Footer 瘦身

```
┌───────────────────────────────────────────────────┐
│ ▌ 输入消息...                                      │  ← 3行输入区
│ deepseek-chat · 1.2k tokens · ¥0.03               │  ← 1行状态条
└───────────────────────────────────────────────────┘
```

- 余额、缓存命中率、上下文压力 → 仅超阈值时显示
- 命令建议 → 独立 overlay（Phase 5）

## Phase 5: 命令面板 overlay

- 按 `/` 时消息区中央弹出浮动列表（非 footer 内联）
- 上下选择，Enter 执行，Esc 关闭
- 类似 VSCode 命令面板风格

## Phase 6: 多视图导航简化

- 去掉数字键提示的 ShowHeader()
- 底部状态条右侧显示当前视图名
- Ctrl+P 弹出视图切换面板
- 保留数字键快捷切换（不展示）

## Phase 7: DevUI Dashboard 精简

- 顶部 8 行状态 → 3 行
- Agents/Spans 改用 tab 切换
- 用 typography + 颜色标记替代 Grid 表格

## 执行顺序

| Phase | 改动 | 风险 | 预估 |
|-------|------|------|------|
| P1    | ~5 文件，找替模式 | 低 | 30min |
| P2    | ~2 文件 | 中 | 45min |
| P3    | ~1 文件核心重写 | 高 | 2h |
| P4    | ~1 文件 | 中 | 1h |
| P5    | ~2 文件 | 中 | 1h |
| P6    | ~1 文件 | 低 | 30min |
| P7    | ~1 文件 | 低 | 30min |
