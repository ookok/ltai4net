---
name: logging-audit
description: 日志审计——结构化日志/级别选择/敏感信息屏蔽/性能影响
license: MIT
---

# Logging Audit 日志审计

审查项目中的日志实践。

## 1. 日志级别
- `Trace` — 开发调试细节，默认关闭
- `Debug` — 诊断信息（函数入口/出口）
- `Information` — 关键业务事件（用户操作、状态变更）
- `Warning` — 非预期但可恢复的情况
- `Error` — 需要人工介入的失败
- `Critical` — 系统级故障（数据库不可用）

## 2. 结构化日志
- 使用 `ILogger<T>` 泛型注入
- 使用模板字符串：`logger.LogInfo("User {UserId} logged in", userId)`
- 不拼接字符串：`logger.LogInfo($"User {userId} logged in")` ❌

## 3. 敏感信息
- 不记录密码、Token、密钥
- 个人身份信息（PII）需脱敏
- 请求/响应体过大时截断

## 4. 性能
- 日志级别检查：`if (logger.IsEnabled(LogLevel.Debug))`
- 不在热路径中记录大量日志
- 异步日志不阻塞主流程

## 5. 可观测性
- 关键路径添加计时日志
- 外部调用记录耗时、成功/失败
- 异常日志包含完整异常信息
