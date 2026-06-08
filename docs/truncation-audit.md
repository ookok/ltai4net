# 截断分类

## A: 风险低（UI预览/摘要截断，有"..."标记）✅ 安全
这些截断用于UI显示预览，标记了"..."，不丢失关键信息：
- ChatAgent.cs:155 — title > 60
- L1State.cs:127 — s.Text > 60
- L1EssentialProvider.cs:46 — snippet > 200
- L3OnDemandProvider.cs:42 — snippet > 250
- L4DeepSearchProvider.cs:51 — snippet > 300
- L6AgentDiaryProvider.cs:43 — snippet > 150
- L6AgentDiaryProvider.cs:84 — summary > 200
- MemoryTools.cs:114,137 — preview > 500 / > 80
- SubagentTools.cs:216 — preview > 80
- AutoTunerService.cs:154 — text > 80
- SystemTools.cs:252 — bodyPreview > 200
- GitTools.cs:66 — msg > 50
- ExternalPositioner.cs:127 — body > 80

## B: 风险中（工具输出截断，有"[truncated]"标记）⚠️ 可接受但有损失
它们截断的是工具输出给LLM的内容，有标记但可能丢失信息：
- FileSystemTools.cs:71 — content[..10000] 无标记
- DocumentTools.cs:338 — 50000 chars + 标记
- DocumentTools.cs:671 — 50000 chars + 标记
- WebTools.cs:202 — 50000 chars + 标记
- TextTools.cs:181 — 20000 chars + 标记
- DatabaseTools.cs:115 — 50000 chars + 标记
- MarkdownTools.cs:19 — 5000 + 标记，23 — 50000 + 标记
- SystemTools.cs:126,139,140,158 — API key显示截断 *这是安全特征，不能改*

## C: 风险高（数据丢失，无标记，截断关键内容）🚨 需修复
这些截断可能丢失对LLM或用户至关重要的信息：
1. **SemanticChunker.cs** — `Chunk(text, 6000)` 硬截断知识文档为6000字符块
2. **ProactiveSuggestStep.cs:122** — `.Take(5)` 只显示前5条建议
3. **Scorer截断** — BFS子图提取只取前N条结果
4. **KbGraph.cs:140/943** — Take(3/20) 图谱遍历截断
5. **InstructionProvider.cs FilterAgentsMd** — MaxLines=50

## D: 身份标识截断（[..8]取SHA/ID前缀）✅ 安全
- 20+处 Git SHA [..8] — 显示短SHA是标准做法
- 多处 Guid [..8] — 临时文件名需要短标识
- CryptoTools.cs:97 — salt[..16] 正确
