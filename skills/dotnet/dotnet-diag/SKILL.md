---
name: dotnet-diag
description: .NET 诊断和调试——性能分析、内存诊断、日志追踪、OTel 集成、MAF 遥测
license: MIT
allowedTools: [ReadFileContent, SearchContent, Glob, ListFiles, RunCommand, WebSearch, SystemInfo]
---

# .NET Diagnostics & Debugging

LTAI4Net 诊断和性能分析技能。

## 遥测架构

```
┌──────────────────────────────────────────────────┐
│  OpenTelemetry (默认 Console, 可选 OTLP)          │
│  ├─ LTAI:Telemetry:OtlpEndpoint 配置 OTLP 导出   │
│  └─ Console exporter 默认开启                     │
├──────────────────────────────────────────────────┤
│  UsageTracker (静态 API)                          │
│  ├─ 记录 Token 用量、请求数、缓存命中              │
│  ├─ CostDisplay — 预估费用                        │
│  └─ BalanceDisplay — 余额                         │
├──────────────────────────────────────────────────┤
│  FailureRecorder                                 │
│  └─ 记录 L1→L2 升级原因和失败模式                 │
└──────────────────────────────────────────────────┘
```

## L1/L2 链路追踪

项目使用 `System.Diagnostics.Activity` 进行分布式追踪：

```csharp
var traceId = GetOrCreateTraceId(); // Activity.Current?.Id ?? Guid.NewGuid()
```

### 追踪工具

| 工具 | 用途 |
|------|------|
| `ltai dashboard` 或 `ltai dash` | 实时仪表盘：Token/缓存/费用 |
| `ltai health` 或 `ltai hc` | 系统健康检查 |
| DebugView / 调试输出 | `System.Diagnostics.Debug.WriteLine` 输出 L1→L2 升级日志 |

### L1→L2 升级追踪

```
L1(text) → BuildL1State() → FSM(gap<0.3 && support<2)
  → SpanRouter (FusionRoute) → L2 精修
    或 FullRegeneration → L2 全量再生

// 调试输出示例
[ChatAgent] L1→L2 auto-upgrade triggered by refusal pattern
[ChatAgent] L1→L2 auto-upgrade triggered by gap=0.15, support=1
```

## 性能分析指南

### 内存

```bash
dotnet dump collect -p <pid>          # 收集 dump
dotnet dump analyze <dump-file>       # 分析 dump
```

### CPU/热点

```bash
dotnet trace collect -p <pid> --profile gc-verbose  # CPU + GC 追踪
dotnet trace report <trace-file> topN                # 热点排名
```

### 关键性能指标（KPIs）

| 指标 | 工具 | 关注点 |
|------|------|--------|
| L1 响应时间 | UsageTracker | Flash 模型应在 <3s 返回 |
| L2 升级率 | FailureRecorder | 升级率 > 30% 需调优 L1 提示词 |
| 缓存命中率 | UsageTracker.CacheHitRate | 目标 > 40% |
| ONNX 嵌入延迟 | LocalEmbedder | 首次加载较慢，预热后 <100ms |
| Token 比率 | UsageTracker.ContextRatio | 上下文压缩效率 |

### 热重载性能

YAML 工作流热重载使用 `FileSystemWatcher`，250ms debounce：
```csharp
// 保存 .yaml/.json 文件后自动触发
// 不会阻塞当前请求，新加载在后台线程完成
```
