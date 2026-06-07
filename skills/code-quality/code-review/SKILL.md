---
name: code-review
description: 代码审查——检查变更的正确性、安全性、性能、风格和测试覆盖
license: MIT
allowedTools: [BuildReviewContext, GroupChanges, MatchReviewRules, RepairReviewPositions, ReflectReviewQuality, ReadFileContent, SearchContent, FindInCode, Glob, DirectoryTree]
---

# Code Review 代码审查

采用 **Open Code Review 确定性工程 → Agent** 分层架构。

## 阶段 1：确定性预处理

```
BuildReviewContext → GroupChanges → MatchReviewRules
```

1. 调用 `BuildReviewContext` 获取分组和规则匹配概览
2. 调用 `GroupChanges` 查看文件关联关系
3. 调用 `MatchReviewRules` 运行内置规则（async void、SQL 注入、硬编码密钥等）

确定性规则覆盖 40+ 常见模式，从 regex 编译层保证零遗漏。规则匹配结果是"信号"而非"定论"。

## 阶段 2：Agent 审查

在确定性上下文之上进行深度分析。按以下维度逐一检查：

### 1. 正确性
- 规则未覆盖的逻辑错误或边界条件遗漏
- 异步代码死锁风险（`.Result`、`.Wait()`）
- 并发竞态条件、锁粒度、线程安全
- API 契约合规性（null 参数、返回值约定）

### 2. 安全性
- 规则未覆盖的越权访问、鉴权绕过
- 用户输入验证和转义完整性
- 敏感数据泄露（日志、错误信息、响应体）
- 依赖供应链风险（新引入的 NuGet 包）

### 3. 性能
- 规则未覆盖的 N+1 查询、循环内 API 调用
- 缓存策略缺失
- IO 模式（同步 vs 异步、批量 vs 逐条）
- 内存分配热点

### 4. 可维护性
- 命名清晰度、函数职责划分
- 架构耦合度、依赖方向
- 可测试性（依赖注入、接口抽象、模拟友好度）
- 文档与注释一致性

### 5. 测试覆盖
- 新增代码是否有对应测试
- 边界条件和异常路径覆盖
- 测试独立性和可重复性
- 快照/契约测试是否匹配变更

## 阶段 3：后处理

1. 调用 `RepairReviewPositions` 修复文件/行号引用精度
2. 调用 `ReflectReviewQuality` 检查覆盖完整性

## 输出格式

```
## Code Review: {变更范围}

### P0 — 必须修复（correctness/security）
- `src/Foo.cs:42` 死锁风险：使用了 .Result ...
  - 建议：await 替换

### P1 — 建议修复（performance/maintainability）
- `src/Foo.cs:88` 可能的 LINQ 多次枚举 ...
  - 建议：.ToList() 一次

### P2 — 建议优化（style/test-coverage）
- `src/Foo.cs:120` 新增方法缺少测试 ...
  - 建议：添加单元测试覆盖边界条件

---
覆盖：4/5 文件 | P0: 1 | P1: 2 | P2: 1 | 评级: good
```
