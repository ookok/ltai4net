# LTAI — LivingTree AI

轻量级 AI 编程助手。基于 Microsoft Agent Framework 1.8.0。

## 快速开始

### 安装

```bash
# Windows
powershell -ExecutionPolicy Bypass -File scripts/install.ps1

# Linux / macOS
chmod +x scripts/install.sh && ./scripts/install.sh
```

### 配置 API Key

```bash
# 设置默认提供商（DeepSeek）
ltai env set DEEPSEEK_API_KEY sk-xxxxx

# 查看所有可用提供商
ltai env
```

### 启动

```bash
# CLI 模式
ltai

# TUI 交互模式
dotnet run --project src/LTAI.TUI

# Desktop 桌面模式
dotnet run --project src/LTAI.Desktop
```

---

## 界面

### TUI (终端界面)

| 按键 | 视图 |
|------|------|
| `1` | 仪表盘 — Token/缓存/费用统计 |
| `2` | 聊天 — AI 对话 |
| `3` | 配置 — Provider/Key/模型管理 |
| `4` | 文件 — 文件浏览器/编辑器 |
| `5` | 技能 — 已加载技能列表 |

### Desktop (桌面应用)

| 快捷键 | 视图 |
|--------|------|
| `Ctrl+1` | 仪表盘 |
| `Ctrl+2` | 聊天 |
| `Ctrl+3` | 代码编辑器 (AvaloniaEdit) |
| `Ctrl+4` | 技能管理 |
| `Ctrl+5` | 配置管理 |

### CLI 命令

| 命令 | 说明 |
|------|------|
| `ltai env` | 查看/导出/导入环境变量 |
| `ltai env set <name> <value>` | 设置 API Key |
| `ltai migrate` | 知识图谱迁移检查 |
| `ltai textpad [path]` | 文件浏览器/编辑器 |
| `ltai dashboard` | 实时统计仪表盘 |

---

## Skills 自定义技能

在 `skills/` 下创建子目录 + `SKILL.md`：

```
skills/my-skill/
├── SKILL.md          ← 技能定义（YAML frontmatter + Markdown 指令）
├── template.md       ← 关联资源（可选）
└── scripts/test.py   ← 关联脚本（可选，需配置脚本运行器）
```

SKILL.md 格式：

```markdown
---
name: my-skill
description: 我的自定义技能
---

# 使用说明

详细指令...
```

系统自动发现并加载 `skills/` 下所有 SKILL.md，去重后注入 `load_skill` / `read_skill_resource` 工具。

---

## 架构

```
┌──────────────────────────────────────────┐
│  L5  IAgent apps                         │
│  ChatAgent, CodeAgent                    │
├──────────────────────────────────────────┤
│  L3-L4  Cognitive + Evolution            │
│  KbGraph, CgGraph, SkillSystem           │
├──────────────────────────────────────────┤
│  L2  Runtime                             │
│  KgStore (SQLite FTS5 + CTE), Reranker   │
├──────────────────────────────────────────┤
│  L1  I/O Layer                           │
│  SecretManager, UsageTracker, Tools      │
├──────────────────────────────────────────┤
│  L0  MicroKernel                         │
│  MultiProviderChatClient, EmbeddingClient │
└──────────────────────────────────────────┘
```

## 存储

- **知识图谱**: `kg.db` (SQLite + FTS5 全文索引 + CTE 图遍历)
- **记忆**: `.livingtree/memories/` (Markdown 文件)
- **会话**: `.livingtree/sessions/` (JSON)
- **技能用量**: `.livingtree/skill_usage.json`

## 构建

```bash
dotnet build
dotnet test
dotnet publish src/LTAI.Cli -o dist/cli
```
