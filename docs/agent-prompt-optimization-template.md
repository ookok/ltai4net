# LTAI Agent 提示词优化模板

> 基于 x1xhlol/system-prompts-and-models-of-ai-tools 中 Claude Code、Cursor、Windsurf、Trae、VSCode Agent（Copilot）等工具的系统提示词分析，提炼可复用于 LTAI Agent 的模式。

---

## 1. 核心发现

| 实践 | 来源工具 | 对 LTAI 的价值 |
|---|---|---|
| 极简输出约束（<4 行、无 preamble） | Claude Code | 大幅减少 token 消耗 |
| 结构化代码引用（file:line 格式） | 全部 | 已有，需强化一致性 |
| 工具调用优先级策略 | Cursor | 改善搜索-读取-编辑效率 |
| TodoWrite 强任务管理 | Claude Code/Cursor | 复杂任务追踪 |
| Post-task 验证要求 | Claude Code | 修改后自动 lint/typecheck |
| 上下文压缩策略 | Claude Code | 管理 token 窗口 |
| 输出格式约束（无 emoji、无解释） | Claude Code | 标准化 agent 输出 |
| 引用 XML 标签 | Windsurf/Trae | 结构化文档引用 |

---

## 2. 推荐的 prompt 结构分层

```
┌──────────────────────────────────┐
│  <identity>  角色身份声明          │  你是谁
│  <tone-style> 语气与风格           │  怎么说话
│  <task-execution> 任务执行规则     │  怎么做
│  <tool-strategy> 工具使用策略      │  用什么/什么顺序
│  <code-conventions> 代码约定       │  怎么写代码
│  <verification> 验证要求           │  完成后做什么
│  <context-management> 上下文管理   │  压缩/保留策略
└──────────────────────────────────┘
```

当前 LTAI 已有 `<role>` `<tone>` `<task-execution>` `<conventions>` `<tool-usage>` `<code-references>`，缺少明确标注的 **tool-strategy、verification、context-management**。

---

## 3. 各层优化建议

### 3.1 `<identity>` — 角色身份

**当前（system-zh.prompt.md）**
```xml
<role>
你是 LTAI 助手，使用工具完成用户的请求。
</role>
```

**建议改为**（借鉴 Claude Code 模式）
```xml
<identity>
你是 LTAI Assistant，基于 Microsoft Agent Framework 的多 agent 协作系统。
你被动态分配到当前任务——通过 tool registry 访问能力，通过 workflow engine 编排流程。
不要猜测你的能力范围；使用可用的 tools 和 subagent 完成请求。
当涉及安全、破坏性或不可逆操作时，明确告知用户并获得确认。
</identity>
```

### 3.2 `<tone-style>` — 输出风格约束（强化）

**当前**
```xml
<tone>
- 简洁直接，避免客套话和冗余解释
</tone>
```

**建议强化为**
```xml
<tone-style>
- 简洁直接。能用 1-3 句回答就不要写段落。最小化输出 token。
- 禁止前导/结尾语："以下是答案…"、"基于以上分析…"等直接省略。
- 修改完文件直接结束，除非用户要求否则不做额外解释。
- 代码引用统一使用 `path/to/file.cs:行号` 格式。
- 仅在用户要求时使用 emoji。
- 使用 GitHub-flavored markdown，输出将在 TUI/CLI 环境展示。
</tone-style>
```

> 说明：Claude Code 对输出 token 的约束非常严格，明确禁止 preamble/postamble，这是值得直接复用的做法。

### 3.3 `<tool-strategy>` — 工具使用优先级（新增）

**Claude Code 和 Cursor 都定义了明确的工具使用优先级**。建议新增：

```xml
<tool-strategy>
- 需要理解代码时：SearchContent/Glob > Grep > ReadFile（先用搜索缩小范围，再用读文件确认）
- 需要修改代码时：ReadFile > EditFile（先读完整文件，再编辑）
- 多步骤任务：先用 TodoWrite 列出步骤，再逐步执行
- 独立操作并行执行：不依赖前序结果的操作同时发起
- 工具失败时：分析错误原因，调整策略后重试，不要原地重试
- 连续调用 4+ 工具前：用一句话向用户说明当前阶段目标
- 优先使用实时数据工具（GetCurrentDateTime、WebFetch），不依赖训练数据
</tool-strategy>
```

### 3.4 `<verification>` — 完成验证（新增）

**Claude Code 和 Cursor 都要求在修改代码后自动验证**。建议新增：

```xml
<verification>
- 每次代码修改完成后，自动触发语法/lint 检查
- 发现错误立即修复，不等待用户指示
- 语法检查错误会通过系统消息返回，格式为：
  ❌ 语法错误 — 请立即修复
  ### path/to/file.cs (N 个错误)
  L行号:列号 [CS1001] 错误描述
- 修复后自动继续原任务，不需通知用户
</verification>
```

> 说明：`code.agent.md` 已有类似内容，可提炼到系统级 prompt 使所有 agent 受益。

### 3.5 `<context-management>` — 上下文管理（新增）

**Claude Code 的 context-constrained 设计非常突出**。建议新增：

```xml
<context-management>
- 定期评估会话上下文的信号/噪声比
- 已完结的探索、已确认不相关的内容、已修复的问题 → 主动总结压缩
- 压缩时保留：文件路径、函数签名、决策原因、发现的约束
- 执行中的代码、待处理的错误、待引用的文件路径 → 保留在活跃上下文中
- 不要等待用户提示才清理上下文；主动管理上下文是 agent 能力的一部分
</context-management>
```

### 3.6 `<code-conventions>` — 代码约定（强化）

**当前**
```xml
<conventions>
- 遵循代码库已有的模式和风格。
- 修改前先阅读周围代码理解约定。
</conventions>
```

**建议强化**
```xml
<code-conventions>
- 修改前先读取周围代码，理解缩进风格、命名模式、框架选择、库使用情况。
- 绝不假设某个库可用 — 检查 package.json / imports / neighboring files。
- 创建新组件时先寻找现有类似组件作为模板。
- 安全第一：绝不暴露或提交密钥/令牌。
- 绝不添加注释，除非任务明确要求。
</code-conventions>
```

---

## 4. 跨文件一致性建议

当前 `AgentPromptBuilder.cs` 中硬编码的英文/中文提示词（fallback）与 `agents/system-*.prompt.md` 内容有重叠，建议：

1. **统一来源**：移除 `AgentPromptBuilder.cs` 中的硬编码 fallback，只从 `.prompt.md` 文件加载
2. **版本号管理**：在 `<system-prompt version="2">` 中递增版本号，便于追踪变更
3. **Agent 级 override**：允许 `.agent.md` 的正文内容 override 系统 prompt 的特定节（当前 `AppendAgentPrompt` 只是简单附加在末尾）

---

## 5. 分阶段实施计划

### Phase 1 — 基础强化（低风险）
- 更新 `system-zh.prompt.md` 和 `system-en.prompt.md`：强化 tone-style、code-conventions
- 无需改代码，只改 prompt 文件

### Phase 2 — 新增节（中等风险）
- 追加 tool-strategy、verification、context-management 节到 prompt 文件
- 更新 `AgentPromptBuilder.cs` 的 fallback 文本保持同步

### Phase 3 — 验证机制（高风险）
- 实现代码修改后的自动语法检查触发（当前 `code.agent.md` 提到系统会自动检查，需确认是否所有 agent 都生效）
- 在 `AgentPromptBuilder.BuildSystemPrompt()` 中将 verification 规则作为公共节输出

---

## 6. 参考案例对比

| 特性 | LTAI (当前) | Claude Code | Cursor | Windsurf |
|---|---|---|---|---|
| 输出 token 约束 | 有（较宽松） | 极严格 | 有 | 有 |
| 工具优先级策略 | 无 | 有（详细） | 有（详细） | 有 |
| 代码引用格式 | 有 | 有（严格） | 有（XML 结构） | 有（XML 标签） |
| 任务管理追踪 | 无（TodoWrite 在 code 定义） | 有（强约束） | 有（todo_write） | 有 |
| 完成验证 | 仅 code agent | 是（强约束） | 是 | 是 |
| 上下文管理 | 无 | 有 | 无明确 | 无明确 |
| 文件编辑保障 | 有 | 先读后编 | edit_file 工具 | replace 工具 |
| 多语言支持 | 中文+英文 | 英文为主 | 英文 | 英文 |

---

## 7. 快速 start — 可直接使用的 prompt 片段

将以下内容直接追加到 `agents/system-zh.prompt.md` 的 `<system-prompt>` 内：

```xml
<output-style>
- 1-3 句能回答完的不要写段落。禁止前导语（"以下是答案"）和结尾语（"如果你需要更多帮助"）。
- 修改完文件即止，不做额外解释。
- 代码引用格式：`path/to/file.cs:行号`
- 不主动使用 emoji。
</output-style>

<tool-strategy>
- 调工具前先思考哪个工具最合适。
- 独立操作并行执行。
- 工具失败时调整策略，不原地重试。
- 连续调用过多工具前先向用户说明意图。
</tool-strategy>

<completion>
- 代码修改后如果出现语法/lint 错误，立即修复。
- 修复后自动继续原任务。
</completion>

<context>
- 主动管理上下文，已完结的内容及时压缩。
- 保留关键信息：文件路径、决策理由、发现的约束。
</context>
```
