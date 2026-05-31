---
name: doc-generator
description: 文档生成——XML 注释/README/变更日志 + Office 文档（Word/Excel/PPT）流水线
license: MIT
allowedTools: [ReadFileContent, WriteFileContent, Glob, BuildDocument]
---

# Doc Generator 文档生成

为指定代码生成规范的文档。

## 1. XML 注释
```csharp
/// <summary>
/// 方法/类的简短说明
/// </summary>
/// <param name="paramName">参数说明</param>
/// <returns>返回值说明</returns>
/// <exception cref="ExceptionType">异常条件</exception>
```

## 2. README 结构
```markdown
# 项目名
简短描述

## 功能特性
- ...

## 快速开始
### 前置条件
### 安装
### 使用

## API
### 方法/类说明

## 贡献指南
```

## 3. 变更日志
```markdown
## [1.1.0] - 2025-01-01
### Added
- 新功能
### Changed
- 变更内容
### Fixed
- 修复内容

## [1.0.0] - 2024-12-01
### Added
- 初始版本
```

## 4. Office 文档生成流水线

使用 `DocGenPipeline` + `OfficeTools` 动态生成 Office 文档（.docx/.xlsx/.pptx）。

### 4.1 端到端生成（BuildDocumentAsync）

```csharp
// 从 KbGraph 检索内容 → 填充模板 → 应用样式 → 写入文件
// 输出格式由路径扩展名自动识别
BuildDocumentAsync(query: "销售数据分析", outputPath: "report.docx")
BuildDocumentAsync(query: "项目计划", outputPath: "plan.pptx", stylesJson: "{...}")
```

适用场景：报告生成、数据分析导出、演示文稿创建、合同/标书自动生成。

### 4.2 模板引擎（RenderTemplate）

模板格式：`{{key}}` 占位符，`{{#section}}...{{/section}}` 条件区块。

```markdown
# {{title}}
## 概述
{{summary}}
{{#details}}
- {{detail}}
{{/details}}
```

条件区块行为：如果 data 中存在对应 key 且非空 → 保留内容、移除标签；
如果 key 缺失或为空 → 整个区块被移除。

### 4.3 内容类型推断（InferContentTypes）

自动识别文本结构并返回类型 JSON：
- `# 标题` → heading (level=1)
- `## 小节` → heading (level=2)
- `- 列表项` → list
- `| A | B |` → table
- ` ```code``` ` → code
- 普通文本 → body

### 4.4 样式定义

默认样式 JSON 包含 title / heading1-3 / body / list / code / tableHeader / tableCell。
可通过 stylesJson 参数自定义：
```json
{
  "title": { "fontSize": 28, "bold": true, "color": "1F4E79" },
  "heading1": { "fontSize": 22, "bold": true, "color": "2E75B6" },
  "code": { "fontFamily": "Consolas", "backColor": "F5F5F5" }
}
```

### 4.5 样式原子化复制

| 工具 | 功能 |
|------|------|
| `ExcelCopyRange(src, range, tgt, cell)` | 跨文件复制 Excel 区域，保留样式索引、共享字符串、边框/字体/填充 |
| `WordCopyStyle(src, tgt)` | 克隆 StyleDefinitionsPart + ThemePart |
| `PptCopyStyle(src, tgt)` | 克隆 SlideMasterPart + ThemePart |

### 4.6 样式读取（GetStyles）

| 工具 | 功能 |
|------|------|
| `ExcelGetStyles(path, sheet, range)` | 读取单元格字体/填充/边框/对齐/数字格式 |
| `WordGetStyles(path)` | 读取段落样式/字体/字号/粗斜体/颜色/对齐 |
| `PptGetStyles(path)` | 读取幻灯片形状样式/填充/运行格式 |

### 4.7 模板持久化

```csharp
// 保存模板到 KbGraph（含样式定义）
SaveTemplateAsync(name: "销售报告", content: "# {{title}}...", stylesJson: "{...}")

// 从 KbGraph 检索已保存模板
LoadTemplateAsync(name: "销售报告")
```

### 4.8 工作流示例

1. 用户要求"生成一份销售数据分析的 Word 报告"
2. `BuildDocumentAsync(query: "销售数据分析", outputPath: "report.docx")` 全自动生成
3. 如需自定义样式 → 先调用 `ExcelGetStyles`/`WordGetStyles`/`PptGetStyles` 读取已有文档样式
4. 通过 `WordCopyStyle`/`PptCopyStyle`/`ExcelCopyRange` 迁移样式到新文档
5. 如需精细化控制 → 调用 `RenderTemplate` 手动填充后再通过 `WordWrite`/`ExcelWrite`/`PptWrite` 写入
