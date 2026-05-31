---
name: performance-profile
description: 性能分析——定位瓶颈/内存/IO/延迟热点，给出优化建议
license: MIT
---

# Performance Profile 性能分析

对代码进行性能诊断，按以下维度逐步排查：

## 1. IO 瓶颈
- 是否有不必要的文件/网络 IO？
- 是否可以用批量操作代替逐条处理？
- 缓存是否合理设置（内存缓存、Redis）？

## 2. 内存
- 是否有大对象频繁分配（Large Object Heap）？
- 字符串拼接是否用了 `StringBuilder`？
- LINQ 是否多次枚举同一集合？
- 是否有内存泄漏（事件未注销、静态集合增长）？

## 3. 并发
- 锁粒度是否过大？
- 是否可以用 `SemaphoreSlim` / `Channel` / `Dataflow` 替代粗粒度锁？
- 是否有不必要的同步上下文流转（`.ConfigureAwait(false)`）？

## 4. 数据库/存储
- 查询是否命中索引？（`EXPLAIN ANALYZE`）
- 是否有 N+1 查询模式？
- 批量操作是否在事务中？

## 5. 热点代码
- 热点路径中是否有反射？（可用 `FunctionPointer` / `source generators` 替代）
- 是否有频繁的 `Regex` 实例化？（用 `static Regex` + `Compiled`）
- LINQ 是否产生不必要的中间分配？（`Select().Where().ToList()`）

## 6. 辅助诊断工具

| 诊断项 | 推荐工具 |
|--------|---------|
| 系统资源概览 | `SystemInfo()` |
| 进程列表 | `ListProcesses(filter)"` |
| 网络延迟/连通性 | `Ping(host)` / `HttpCheck(url)` |
| 搜索热点模式 | `SearchContent(pattern: "for\\s*\\(|foreach|while", glob: "*.cs")` |
| 搜索大对象分配 | `SearchContent(pattern: "new\\s+(byte|char)\\[\\d{4,}", glob: "*.cs")` |

## 推荐的优化优先级

```
P0: 修复正确性问题（数据丢失、竞态条件）
P1: 消除 N+1 查询、减少锁竞争
P2: 引入缓存、批处理
P3: 代码级微优化（字符串、LINQ、反射）
```
