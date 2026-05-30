---
name: error-handling
description: 错误处理审查——异常类型/空值处理/边界情况/fail-fast 原则
license: MIT
---

# Error Handling 错误处理审查

审查代码中的错误处理模式是否符合最佳实践。

## 1. 异常类型
- 使用有语义的异常类型：`ArgumentNullException`、`InvalidOperationException`
- 自定义异常继承 `Exception`，后缀 `Exception`
- 不抛出 `Exception` 基类

## 2. 空值处理
- 参数校验：入口处 `Throw.IfNull()` 或 `ArgumentNullException.ThrowIfNull()`
- 返回值：用 `null` 表示"无结果"、用异常表示"错误"
- Nullable 启用时避免不必要的 null 检查

## 3. 异常处理原则
- 只捕获你能处理的异常
- 捕获后记录日志再抛（`throw;` 保留堆栈）
- 不在 catch 中吞掉异常（空 catch 加注释说明原因）
- 异步方法中避免 `.Result` / `.Wait()`（死锁风险）

## 4. 边界情况
- 空集合：`Enumerable.Empty<T>()` 而非 `null`
- 空字符串：`string.IsNullOrEmpty()` 校验
- 超大输入：设置合理上限并主动拒绝

## 5. Fail-Fast
- 启动时验证配置有效性
- 不可恢复的错误尽早失败
- 使用 `Environment.FailFast()` 处理致命异常
