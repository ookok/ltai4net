---
name: ppt-maker
description: PPT 生成——生成演示文稿大纲/内容/演讲备注/Marp 或 Reveal.js 格式
license: MIT
---

# PPT Maker 演示文稿生成

生成可直接用于演示工具的幻灯片内容。

## 1. 输出格式

默认输出 **Marp** 格式（Markdown 转 PPT），可直接在 VS Code 中预览：

```markdown
---
marp: true
theme: uncover
class: lead
---

# 标题

作者名

---

## 目录

1. 背景
2. 方法
3. 结果
4. 结论
```

也可选择 **Reveal.js** HTML 格式：

```html
<section>
  <h2>标题</h2>
  <p>内容</p>
</section>
```

## 2. 幻灯片结构

| 页数 | 内容 |
|:----:|------|
| 1 | 标题页：标题 + 副标题 + 作者 |
| 2 | 目录/议程 |
| 3-N | 正文（每页一个核心观点） |
| N+1 | 总结/结论 |
| N+2 | Q&A / 联系方式 |

## 3. 设计原则
- 每页不超过 7 行文字
- 核心数据用图表展示（`<table>` / `<chart>`）
- 关键数字加粗或放大字号
- 使用 `---` 分隔幻灯片

## 4. 演讲备注

用 `<!-- presenter notes -->` 添加备注：

```markdown
## 技术架构

<!-- 此处口头展开：讲清楚架构设计的三个关键决策 -->

- 前端: React + TypeScript
- 后端: .NET 10 + PostgreSQL
```

## 5. Office 原生 PPT 生成

如需直接生成 `.pptx` 文件，使用以下工具链：

### 5.1 PptWrite — 快速创建
每行文本作为一页幻灯片，自动创建默认母版/布局/主题。

```csharp
PptWrite(path: "demo.pptx", content: "标题页\n目录\n详细内容", create: true)
```

### 5.2 PptCopyStyle — 主题迁移
从已有 PPT 复制母版/主题到新文件，统一视觉风格：
```csharp
PptCopyStyle(srcPath: "template.pptx", tgtPath: "output.pptx")
```

### 5.3 PptGetStyles — 样式分析
分析已有 PPT 的形状填充、运行字体/颜色：
```csharp
PptGetStyles(path: "参考.pptx")
```

### 5.4 DocGenPipeline — 端到端流水线
从 KbGraph 检索内容 → 自动分组为幻灯片 → 应用样式 → 写入 `.pptx`：
```csharp
BuildDocumentAsync(query: "项目总结", outputPath: "summary.pptx")
```

流水线 `GroupBySlide` 逻辑：
- 内容中的 `#`/`##` 标题 → 幻灯片标题
- 标题下的正文/列表/代码块 → 幻灯片正文区域
- 无标题的纯内容 → 自动分组到 "Notes" 页

### 5.5 推荐工作流
1. 先用 Marp/Reveal.js 快速起草内容结构（本 skill 第 1-3 节）
2. 再用 `PptCopyStyle` 从正式模板迁移主题
3. 最后用 `PptWrite` 写入内容
4. 或一步到位：`BuildDocumentAsync(query, "presentation.pptx")`
