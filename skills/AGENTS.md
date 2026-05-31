# Skill 编写指南

每个 Skill 是一个独立的子目录，包含 `SKILL.md` 主文件，可选 `*.csx` 脚本和资源文件。

## 目录结构

```
skills/<domain>/<skill-name>/
├── SKILL.md        # 技能定义（必需）
├── run.csx         # C# 脚本（可选，Roslyn Scripting 执行）
└── *.csx           # 其他辅助脚本（可选）
```

## SKILL.md 格式

```yaml
---
name: <技能名称>                          # 必需，LLM 通过此名称 load_skill
description: <一句话描述>                 # 必需，LLM 据此判断是否匹配任务
license: MIT                             # 可选
allowedTools: [Tool1, Tool2]             # 可选，技能依赖的工具列表
---

# 标题
... markdown 指令正文 ...
```

## allowedTools 指引

`allowedTools` 告诉 Tool RAG 这个技能需要哪些工具。Tool RAG 只保留 top-8 最相关的工具，如果技能需要的工具被过滤掉，LLM 加载了 skill 也无法执行。

### 常用工具参考

| 工具 | 用途 | 适用技能 |
|------|------|----------|
| `ReadFileContent` | 读取文件内容 | 所有分析类技能 |
| `SearchContent` | 全文搜索（grep） | 代码审查、迁移 |
| `FindInCode` | 精确查找标识符定义/引用 | 重构、审计 |
| `Glob` | 按模式查找文件 | 文件遍历 |
| `DirectoryTree` | 目录树 | 架构分析 |
| `ListFiles` | 列出目录文件 | 通用 |
| `WriteFileContent` | 写文件 | 文档生成 |
| `BuildDocument` | 生成 Office 文档 | 文档生成 |
| `WebSearch` | 网络搜索 | 竞品分析 |
| `RunCommand` | 执行 shell 命令 | Git 工作流、性能分析 |
| `CSharpScript` | 执行 C# 脚本 | 数据分析 |

### 规则
- 不写 `allowedTools` = 所有工具可用（Tool RAG 行为不变）
- 写了 `allowedTools` = Tool RAG 会优先保留这些工具
- 不要列出 `load_skill`（自动可用）

## 脚本（可选）

如果技能需要自动化执行，可以在同目录下放 `run.csx`（C# Script）：

```csharp
#r "nuget: Some.Package, 1.0.0"
using System.Text.Json;

var input = args.Length > 0 ? args[0] : "";
var data = JsonSerializer.Deserialize<JsonElement>(input);
// ... 你的逻辑 ...
return JsonSerializer.Serialize(result);
```

脚本通过 `SkillScriptRunner.RunAsync` 由 `dotnet script` 或 `dotnet run` 执行，60 秒超时。

## 最佳实践

1. **description 要精准** — LLM 根据 description 决定是否加载此 skill，描述要清晰且独特
2. **allowedTools 要完整** — 列出技能执行所需的所有核心工具
3. **正文结构化** — 使用 `## 标题` + 列表 + 表格，LLM 容易遵循
4. **输出格式明确** — 在 `## 输出格式` 中给出 JSON 或 Markdown 模板
5. **示例优先** — 提供输入/输出示例，LLM 表现更好
