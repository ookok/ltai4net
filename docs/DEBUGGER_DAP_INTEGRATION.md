# Desktop 调试器集成设计（DAP 方案）

## 目标

在 LTAI.Desktop TextPadView 中添加**断点单步调试**能力，支持 C# (.NET) 项目，预留 Python/JS/Go 扩展点。

## 架构总览

```
┌─────────────────────────────────────────────────┐
│  TextPadView (AvaloniaEdit)                     │
│  ┌───┬──────────────────────────────────────┐   │
│  │ B │  LineNumber  │  Code  │  Folding     │   │
│  │ P │  Margin      │  Text  │  Margin      │   │
│  │ M │             │        │              │   │
│  │ r │             │        │              │   │
│  │ g │             │        │              │   │
│  └───┴──────────────────────────────────────┘   │
│  ┌─────────────────────────────────────────┐    │
│  │  CallStackView │ VariablesView          │    │
│  └─────────────────────────────────────────┘    │
└─────────────────────────────────────────┬───────┘
                                          │ DAP JSON-RPC
                                          ▼
┌──────────────────────────────────────────────┐
│  DapClient (src/LTAI.Desktop/Debugging/)     │
│  ┌─────────────┐  ┌──────────────────────┐  │
│  │ JsonRpc     │  │ DapSession (state    │  │
│  │ (Stream)    │  │ machine)             │  │
│  └─────────────┘  └──────────────────────┘  │
│  ┌──────────────────────────────────────┐  │
│  │ BreakpointManager (file↔bps map)     │  │
│  └──────────────────────────────────────┘  │
└──────────────────────┬───────────────────┘
                       │ stdio / TCP
                       ▼
┌───────────────────────────────┐
│  Debug Adapter (外部进程)     │
│  dotnet debug --debug-adapter │
│  debugpy --listen ...         │
│  node --inspect-brk ...       │
└───────────────────────────────┘
```

## 新增文件清单

| 文件 | 约行数 | 说明 |
|---|---|---|
| `Debugging/DapClient.cs` | ~300 | JSON-RPC 2.0 + DAP 协议传输层 |
| `Debugging/DapSession.cs` | ~400 | 调试会话状态机（launch/attach/breakpoint/step/continue） |
| `Debugging/BreakpointManager.cs` | ~250 | 断点数据模型 + JSON 文件持久化 |
| `Debugging/BreakpointMargin.cs` | ~200 | `AbstractMargin` 子类，边栏红点渲染 + 点击切换 |
| `Debugging/CallStackView.cs` | ~250 | 调用栈树（Avalonia TreeView） |
| `Debugging/VariablesView.cs` | ~300 | 变量/监视窗口（可展开 TreeView） |
| `Debugging/DebugToolbar.cs` | ~150 | 顶部工具栏（Continue/StepOver/StepInto/StepOut/Restart/Stop） |
| **合计** | **~1,850** | |

## DAP 协议

### 支持的 DAP 请求

| DAP 请求 | 触发时机 |
|---|---|
| `initialize` | 调试会话启动 |
| `launch` | 用户点 Run/Debug |
| `setBreakpoints` | 断点行变化 |
| `setExceptionBreakpoints` | 加载配置时 |
| `configurationDone` | 初始化完成后 |
| `continue` | 用户点 Continue |
| `next` | Step Over |
| `stepIn` | Step Into |
| `stepOut` | Step Out |
| `pause` | 暂停执行 |
| `terminate` | 停止调试 |
| `disconnect` | 关闭会话 |
| `threads` | 获取线程列表 |
| `stackTrace` | 暂停后获取调用栈 |
| `scopes` | 获取栈帧的作用域 |
| `variables` | 展开作用域/变量 |
| `evaluate` | Watch 窗口表达式求值 |

### 处理的事件

| DAP 事件 | 行为 |
|---|---|
| `stopped` (breakpoint) → 高亮当前行 + 刷新 CallStackView + VariablesView | 暂停 |
| `stopped` (step) → 同上 | 单步停止 |
| `stopped` (exception) → 同上 + 异常提示 | 异常断住 |
| `continued` → 清高亮 | 继续 |
| `output` (stdout/stderr/console) → 追加到 terminal | 输出重定向 |
| `terminated` → 清理调试 UI | 进程结束 |
| `exited` → 同上 | 退出 |

### 数据模型

```csharp
namespace LTAI.Desktop.Debugging;

// 断点
public sealed record Breakpoint(
    string File,           // 绝对路径
    int Line,              // 1-indexed
    int Column = 0,
    bool IsEnabled = true,
    string? Condition = null,
    string? HitCondition = null);

// DAP 适配器配置
public sealed record DebugAdapterConfig(
    string Command,     // e.g. "dotnet", "python", "node"
    string[] Args,      // e.g. ["debug", "--debug-adapter"], ["-m", "debugpy", "--listen", "5678"]
    string? Runtime);   // null = auto, or "dotnet"/"python"/"node"

// 作用域 + 变量
public sealed record DapVariable(
    string Name,
    string Value,
    string Type,
    int VariablesReference,   // 0 = leaf, >0 = expandable
    DapVariable[]? Children = null);

// 栈帧
public sealed record DapStackFrame(
    int Id,
    string Name,
    string? File,
    int Line,
    int Column);

// 线程
public sealed record DapThread(int Id, string Name);
```

## TextPadView 集成点

### 1. BreakpointMargin（断点边栏）

```
插入位置：LeftMargins 最左侧
class BreakpointMargin : AbstractMargin
```

- `OnRender(DrawingContext ctx)` — 遍历当前可见行，有断点画红点（已启用）或空心圆圈（已禁用），当前暂停行画黄色箭头
- `PointerPressed` — 点击切换断点（`BreakpointManager.Toggle(line)`）
- `MouseMove` — 悬停断点行时显示条件文本
- 依赖 `BreakpointManager` 获取 `(file, line) → Breakpoint?`

### 2. 暂停状态行高亮

```csharp
// 在 editor.PointerHover 或 TextArea.Caret 无直接集成，需：
// 用 editor.TextArea.TextView.LineTransformers 添加
// class CurrentLineHighlighter : DocumentColorizingTransformer
// 对 pausedLine 整行设置黄色背景
```

### 3. DebugToolbar（顶部工具栏）

```
[▶ Continue] [⤵ Step Over] [↷ Step Into] [↶ Step Out] [⟳ Restart] [■ Stop]  [● LTAI-Console]
```

- 所有按钮仅调试活跃时启用
- 状态标签显示当前线程 + 文件名:行号

### 4. CallStackView（底部左侧面板）

`TreeView`，每行：`FrameIcon + 方法名 + 文件名:行`
- 双击跳转到对应文件+行（`OpenFileAndScrollTo(file, line)`）
- 选中后刷新 VariablesView

### 5. VariablesView（底部右侧面板）

`TreeView`，可展开，每行：`变量名 | 值 | 类型`
- 展开 VariablesReference>0 的变量查看子字段
- Watch 输入框（evaluate 表达式）
- 值支持语法着色（string=绿色, number=黄色, bool=蓝色, null=灰色）

## 断点持久化

**文件位置：`<workspace>/.livingtree/launch.vs.json`**（与 LTAI 现有 .livingtree 位置一致）

```json
{
  "version": 1,
  "configurations": [
    {
      "name": "LTAI.Web",
      "type": "dotnet",
      "project": "src/LTAI.Web/LTAI.Web.csproj",
      "args": []
    },
    {
      "name": "LTAI.Desktop",
      "type": "dotnet",
      "project": "src/LTAI.Desktop/LTAI.Desktop.csproj",
      "args": []
    }
  ],
  "breakpoints": [
    { "file": "src/LTAI.Agent/Agents/ChatAgent.cs", "line": 235, "enabled": true, "condition": "" }
  ]
}
```

- 由 `BreakpointManager` 读写在 `.livingtree/launch.vs.json` 中
- 自动检测项目根（根据 `program.csproj` / `.sln` 等）
- 断点跨重启持久化

## 适配器自动检测

项目类型自动匹配适配器：

| 检测依据 | type | 命令 |
|---|---|---|
| `*.csproj` + `"Exe"` / `<OutputType>Exe</OutputType>` | `dotnet` | `dotnet debug --debug-adapter` |
| `requirements.txt` 或 `*.py` 为主 | `python` | `python -m debugpy --listen 5678 <script>` |
| `package.json` 含 `"main"` 或 `"scripts"` | `node` | `node --inspect-brk <script>` |
| `go.mod` | `go` | `dlv debug --headless --listen=:2345 --api-version=2` |

## 阶段计划

| 阶段 | 文件 | 工时 |
|---|---|---|
| P1: DapClient + DapSession | `DapClient.cs`, `DapSession.cs` | 1 周 |
| P2: BreakpointManager + BreakpointMargin | `BreakpointManager.cs`, `BreakpointMargin.cs` | 0.5 周 |
| P3: CallStackView + VariablesView | `CallStackView.cs`, `VariablesView.cs` | 1 周 |
| P4: DebugToolbar + TextPadView 集成 | `DebugToolbar.cs`, 改 `TextPadView.cs` | 0.5 周 |
| P5: 持久化 + 配置 UI + 适配器自动检测 | 改 `ConfigDialog.cs` 等 | 0.5 周 |
| **总计** | | **~3.5 周** |
