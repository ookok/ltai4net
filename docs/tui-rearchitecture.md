# LTAI.TUI 架构改造方案 —— 仿 opencode（OpenTUI + SolidJS）

> 目标：在保留 .NET / Spectre.Console 的前提下，全面引入 opencode 的声明式组件体系、响应式状态、帧循环渲染和键绑定系统。

---

## 1. 总体架构对比

| 维度 | opencode (OpenTUI + SolidJS) | LTAI.TUI 现状 | LTAI.TUI 改造后 |
|---|---|---|---|
| UI 范式 | 声明式 JSX 组件树 | 命令式 Panel 拼接 | 声明式组件树 |
| 渲染驱动 | 反应式信号 → 帧循环差分 | 轮询脏标记 | 信号驱动 → 帧循环差分 |
| 布局 | Yoga Flexbox | Spectre.Console Layout | 仿 Flexbox 布局引擎 |
| 输入 | Keymap 模式栈 + useBindings | 双线程 ReadKey + 大状态机 | KeymapEngine 模式栈 |
| 对话框 | zIndex 叠加层 | 替换 Messages 面板内容 | OverlayManager 叠加层 |
| 主题 | 反应式 Proxy + SyntaxStyle | 静态 ThemeService | 反应式 ThemeSignal |
| 组件 | SolidJS 函数组件 | ChatLayout 单文件 1100 行 | 拆分 15+ 小组件 |

### 渲染流水线对比

```
opencode:                                   LTAI.TUI 改造后:
JSX 组件树                                  IComponent 树
    ↓ Solid 反应式信号                            ↓ Signal/Memo 反应式
Renderable 树                                RenderContext 树
    ↓ Yoga 布局                                  ↓ Flexbox 布局
Cell Buffer (2D 网格)                          CellBuffer (2D 网格)
    ↓ Diff 计算                                    ↓ 脏区域计算
ANSI Escape Codes                             Spectre.Console IRenderable
    ↓ stdout                                       ↓ AnsiConsole.Live
Terminal                                     Terminal
```

---

## 2. 组件体系

### 2.1 IComponent 接口

```csharp
public interface IComponent
{
    string Id { get; }
    IComponent? Parent { get; set; }
    IReadOnlyList<IComponent> Children { get; }

    // 测量阶段：计算组件期望尺寸
    Size Measure(Size available);

    // 布局阶段：分配实际位置和尺寸
    void Layout(Rect bounds);

    // 渲染阶段：生成 IRenderable
    IRenderable Render(IRenderContext ctx);

    // 挂载/卸载生命周期
    void Mount();
    void Unmount();
}
```

### 2.2 基础组件

| 组件 | 对应 opencode | 说明 |
|---|---|---|
| `Box` | `<box>` | Flexbox 容器，支持 flexDirection/grow/shrink/gap/padding/border |
| `Text` | `<text>` | 文本显示，支持前景色/属性 |
| `ScrollBox` | `<scrollbox>` | 可滚动容器，支持虚拟滚动 |
| `TextInput` | `<textarea>` | 多行输入，支持光标/选择/placeholder |
| `Stack` | (implicit `<box>`) | 纵向/横向等间距排列 |
| `Overlay` | `position="absolute"` | 绝对定位叠加层 |
| `Spacer` | `<box flexGrow />` | 弹性空白填充 |

### 2.3 Flexbox 布局引擎

实现简化的 Yoga 语义，映射到 Spectre.Console 的渲染能力：

```csharp
public class Box : ContainerComponent
{
    public FlexDirection Direction { get; set; }      // Row | Column
    public int FlexGrow { get; set; }
    public int FlexShrink { get; set; }
    public JustifyContent Justify { get; set; }
    public int Gap { get; set; }
    public Padding Padding { get; set; }
    public BoxBorder? Border { get; set; }
    public Color? Background { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? MinWidth { get; set; }
    public int? MinHeight { get; set; }
    public int ZIndex { get; set; }                   // 用于叠加层
}
```

布局算法（简化 flexbox）：

```
1. Measure 阶段：
   - 每个子组件收到可用尺寸
   - flexGrow>0 的子组件用 min 尺寸，其余用 preferred 尺寸
   - 剩余空间按 flexGrow 比例分配

2. Layout 阶段：
   - 按 Direction 排列子组件
   - 应用 Gap/Justify/Padding
   - 设置每个子组件的 Bounds

3. Render 阶段：
   - 按 ZIndex 排序（默认 0，叠高层 >0）
   - 依次渲染子组件
   - 用 Spectre.Console Panel/Table 实现
```

---

## 3. 响应式状态系统

### 3.1 Signal<T>

仿 SolidJS `createSignal`：

```csharp
public class Signal<T> : IObservable<T>
{
    private T _value;
    private int _version;
    private readonly List<Action<T>> _subscribers = new();

    public T Value
    {
        get {
            Effect.Track(this);       // 在 Effect 中自动追踪
            return _value;
        }
        set {
            if (!EqualityComparer<T>.Default.Equals(_value, value))
            {
                _value = value;
                _version++;
                Notify();
            }
        }
    }

    // 批量更新时延迟通知
    public void SetWithoutNotify(T value) { _value = value; _version++; }

    public void Notify() {
        foreach (var sub in _subscribers) sub(_value);
    }

    public IDisposable Subscribe(Action<T> callback) { ... }
    public IDisposable SubscribeWeak(Action<T> callback) { ... }
}

// 工厂方法（仿创造函数）
public static class Signals
{
    public static Signal<T> Create<T>(T initial) => new(initial);
    public static Memo<T> Memo<T>(Func<T> compute) => new(compute);
    public static Effect Effect(Action fn) => new(fn);
}
```

### 3.2 Memo<T>

派生信号，自动追踪依赖：

```csharp
public class Memo<T> : IObservable<T>
{
    private T _value;
    private int _version;
    private readonly Func<T> _compute;
    private readonly Effect _effect;

    public Memo(Func<T> compute)
    {
        _compute = compute;
        _effect = new Effect(() => {
            var newValue = _compute();
            if (!EqualityComparer<T>.Default.Equals(_value, newValue)) {
                _value = newValue;
                _version++;
                Notify();
            }
        });
    }

    public T Value {
        get {
            Effect.Track(this);
            return _value;
        }
    }
}
```

### 3.3 Effect

自动追踪依赖并执行副作用：

```csharp
public class Effect : IDisposable
{
    private static readonly AsyncLocal<Effect?> _current = new();
    private readonly Action _fn;
    private readonly List<(object source, Action unsubscribe)> _deps = new();
    private bool _disposed;

    public Effect(Action fn) { _fn = fn; Schedule(); }

    public static void Track(IObservable observable) {
        var current = _current.Value;
        if (current != null) current.TrackInternal(observable);
    }

    public void Schedule() {
        // 在主帧循环中执行
        RenderLoop.Schedule(this);
    }

    // 执行时：清除旧依赖 → 设置 _current → 运行 fn → 清除 _current
    public void Execute() {
        UnsubscribeAll();
        _current.Value = this;
        try { _fn(); }
        finally { _current.Value = null; }
    }
}
```

### 3.4 Batch 批量更新

批量合并信号通知：

```csharp
public static class Batch
{
    private static readonly AsyncLocal<int> _depth = new();

    public static void Begin() => _depth.Value++;
    public static void End() {
        if (--_depth.Value == 0) Flush();
    }

    public static void Run(Action fn) {
        Begin();
        try { fn(); }
        finally { End(); }
    }
}
```

### 3.5 状态管理使用模式

```csharp
// 在组件中
public class AppComponent : Component
{
    private readonly Signal<string> _input = Signals.Create("");
    private readonly Signal<bool> _processing = Signals.Create(false);
    private readonly Memo<bool> _canSend;
    private readonly Memo<string> _statusText;

    public AppComponent()
    {
        _canSend = Signals.Memo(() =>
            _input.Value.Length > 0 && !_processing.Value
        );

        _statusText = Signals.Memo(() =>
            _processing.Value ? "处理中..." : "就绪"
        );
    }
}
```

---

## 4. 帧循环渲染

### 4.1 RenderLoop

仿 opencode 的 60fps 帧循环 + 脏区域差分：

```csharp
public class RenderLoop : IDisposable
{
    private readonly LiveDisplayContext _liveCtx;
    private readonly IComponent _root;
    private IRenderable? _previousRender;
    private int _targetFps = 60;
    private CancellationTokenSource _cts;
    private readonly ConcurrentQueue<Action> _effects = new();
    private static readonly AsyncLocal<RenderLoop?> _current = new();

    public static RenderLoop? Current => _current.Value;

    public RenderLoop(LiveDisplayContext ctx, IComponent root)
    {
        _liveCtx = ctx;
        _root = root;
        _cts = new();
    }

    public async Task RunAsync()
    {
        _current.Value = this;
        var frameInterval = 1000 / _targetFps;
        var stopwatch = Stopwatch.StartNew();

        try {
            _root.Mount();
            while (!_cts.IsCancellationRequested)
            {
                var elapsed = stopwatch.ElapsedMilliseconds;
                var needsRefresh = false;

                // 1. 执行待处理的 Effect
                while (_effects.TryDequeue(out var effect))
                {
                    effect();
                    needsRefresh = true;
                }

                // 2. 检查信号版本（快速跳过）
                if (Signal.GlobalVersion == _lastRenderVersion)
                {
                    await Task.Delay(50);
                    continue;
                }

                // 3. 执行完整渲染
                if (needsRefresh || Signal.GlobalVersion != _lastRenderVersion)
                {
                    var renderCtx = new RenderContext {
                        Bounds = new Rect(0, 0, Console.WindowWidth, Console.WindowHeight)
                    };
                    _root.Layout(renderCtx.Bounds);
                    var currentRender = _root.Render(renderCtx);
                    _liveCtx.Refresh();
                    _lastRenderVersion = Signal.GlobalVersion;
                    _previousRender = currentRender;
                }

                // 4. 帧同步
                var sleep = (int)Math.Max(1, frameInterval - stopwatch.ElapsedMilliseconds + elapsed);
                await Task.Delay(Math.Min(sleep, 50));
            }
        }
        finally {
            _root.Unmount();
            _current.Value = null;
        }
    }

    public static void Schedule(Effect effect) {
        Current?._effects.Enqueue(effect.Execute);
    }
}
```

### 4.2 差分渲染策略

采用**两级差分**：

| 级别 | 粒度 | 策略 |
|---|---|---|
| **组件树差分** | 组件级 | 比较组件版本号，跳过未变化的子树 |
| **IRenderable 缓存** | IRenderable 级 | 组件缓存上次 IRenderable，信号 VERSION 未变直接复用 |

```csharp
public abstract class Component : IComponent
{
    private int _lastRenderVersion = -1;
    private IRenderable? _cachedRender;

    public IRenderable Render(IRenderContext ctx)
    {
        var currentVersion = Signal.GlobalVersion;
        if (currentVersion == _lastRenderVersion && _cachedRender != null)
            return _cachedRender;

        _cachedRender = RenderCore(ctx);
        _lastRenderVersion = currentVersion;
        return _cachedRender;
    }

    protected abstract IRenderable RenderCore(IRenderContext ctx);
}
```

---

## 5. Overlay / 对话框系统

### 5.1 OverlayManager

仿 opencode `DialogProvider` + `useDialog()`：

```csharp
public class OverlayManager
{
    private readonly List<OverlayEntry> _stack = new();

    public void Push(IComponent overlay, int zIndex = 1000)
    {
        _stack.Add(new OverlayEntry(overlay, zIndex));
        Invalidate();
    }

    public void Pop()
    {
        if (_stack.Count > 0) _stack.RemoveAt(_stack.Count - 1);
        Invalidate();
    }

    public void Clear()
    {
        _stack.Clear();
        Invalidate();
    }

    public void Replace(IComponent overlay)
    {
        Clear();
        Push(overlay);
    }

    // 在根组件中调用，渲染叠加层
    public IEnumerable<IComponent> GetOverlays() =>
        _stack.OrderBy(e => e.ZIndex).Select(e => e.Component);

    public event Action? Changed;
    private void Invalidate() => Changed?.Invoke();
}
```

### 5.2 叠加层渲染

在根 `Box` 组件的 `RenderCore` 中：

```csharp
protected override IRenderable RenderCore(IRenderContext ctx)
{
    var children = new List<IRenderable>();

    // 1. 渲染主内容
    foreach (var child in Children)
        children.Add(child.Render(ctx));

    // 2. 渲染叠加层（覆盖在主内容之上）
    if (_overlayManager.HasOverlays)
    {
        // 使用 Spectre.Console 的布局叠加
        // 用全屏 Panel 作为背景遮罩
        var overlayCanvas = new Canvas(ctx.Bounds.Width, ctx.Bounds.Height);
        foreach (var overlay in _overlayManager.GetOverlays())
        {
            var overlayRender = overlay.Render(ctx);
            // 叠加渲染（居中或全屏）
            children.Add(new Align(overlayRender, VerticalAlignment.Middle, HorizontalAlignment.Center));
        }
    }

    return new Columns(children); // 或自定义布局
}
```

**改进方案**：使用 Spectre.Console `RenderPipeline` + `LiveDisplayContext` 实现更精确的叠加。每帧重建布局树。

---

## 6. 键绑定系统

### 6.1 KeymapEngine

仿 opencode 的 `@opentui/keymap` + mode stack：

```csharp
public class KeymapEngine
{
    private readonly Stack<string> _modeStack = new();
    private readonly List<KeyBinding> _bindings = new();
    private string _pendingSequence = "";

    // 模式栈
    public void PushMode(string mode) => _modeStack.Push(mode);
    public void PopMode() => _modeStack.Pop();
    public string CurrentMode => _modeStack.Count > 0 ? _modeStack.Peek() : "base";

    public void Register(KeyBinding binding) => _bindings.Add(binding);

    public bool HandleKey(ConsoleKeyInfo key)
    {
        // 1. 构建当前有效绑定（按模式栈筛选）
        var activeModes = new[] { CurrentMode, "base" };
        var candidates = _bindings
            .Where(b => activeModes.Contains(b.Mode))
            .OrderByDescending(b => b.Priority);

        // 2. Leader key 序列处理
        _pendingSequence += KeyName(key);
        foreach (var binding in candidates)
        {
            if (binding.Matches(_pendingSequence))
            {
                _pendingSequence = "";
                binding.Execute();
                return true;
            }
        }

        // 3. 普通键直接匹配
        foreach (var binding in candidates.Where(b => !b.IsSequence))
        {
            if (binding.Matches(key))
            {
                binding.Execute();
                return true;
            }
        }

        return false;
    }
}

public class KeyBinding
{
    public string Mode { get; init; } = "base";
    public int Priority { get; init; } = 0;
    public string? Sequence { get; init; }    // 多键序列（如 "ctrl+x ctrl+s"）
    public ConsoleKey? Key { get; init; }
    public ConsoleModifiers? Modifiers { get; init; }
    public string? Description { get; init; }
    public Action? Execute { get; init; }
    public Func<bool>? Enabled { get; init; }

    public bool Matches(ConsoleKeyInfo k) =>
        Key == null || (k.Key == Key &&
            (!Modifiers.HasValue || k.Modifiers == Modifiers));

    public bool Matches(string seq) =>
        Sequence != null && seq == Sequence;
}
```

### 6.2 模式栈定义

| 模式 | 说明 | 激活时机 |
|---|---|---|
| `base` | 全局导航 | 始终 |
| `input` | 文本输入 | 输入框聚焦 |
| `picker` | 命令选择器 | `/` 按下 |
| `cascade` | 级联菜单 | 多级命令 |
| `overlay` | 对话框 | Overlay 弹出 |
| `search` | 搜索模式 | Ctrl+F |
| `autocomplete` | 自动补全 | 弹出补全列表 |

### 6.3 命令注册方式

```csharp
// 在组件初始化中
_keymap.Register(new KeyBinding {
    Mode = "base",
    Key = ConsoleKey.P,
    Modifiers = ConsoleModifiers.Control,
    Description = "视图切换器",
    Execute = () => _overlayManager.Push(new ViewSwitcherOverlay())
});

_keymap.Register(new KeyBinding {
    Mode = "input",
    Key = ConsoleKey.Enter,
    Execute = SubmitMessage,
    Enabled = () => _input.Value.Length > 0
});
```

---

## 7. 组件树结构（新架构）

```
AppComponent (Box, flexDirection: Column)
├── MainContent (Box, flexGrow: 1)
│   ├── [Route: Home]
│   │   └── HomeScreen (Box, alignItems: Center)
│   │       ├── Logo (Text)
│   │       ├── Spacer
│   │       └── PromptInput (TextInput, maxWidth: 75%屏宽)
│   │
│   └── [Route: Session]
│       └── ChatSession (Box, flexDirection: Row)
│           ├── MessageList (ScrollBox, flexGrow: 1)
│           │   ├── MessageBubble (user) × N
│           │   └── MessageBubble (assistant) × N
│           │       ├── TextPart
│           │       ├── ToolPart
│           │       └── ReasoningPart
│           ├── PermissionPrompt / QuestionPrompt
│           └── PromptInput (TextInput)
│
├── StatusBar (Box, flexShrink: 0)
│   ├── ModelLabel (Text)
│   ├── TokenUsage (Text)
│   └── ViewName (Text)
│
└── OverlayLayer (Box, position: Absolute, zIndex: 3000)
    ├── [When OverlayManager.HasOverlays]
    │   └── Dialog (Box, alignItems: Center)
    │       ├── DialogBackdrop (Box, background: semi-transparent)
    │       └── DialogContent (Box, centered)
    │           ├── CommandPicker
    │           ├── TextInputOverlay
    │           ├── QuestionOverlay
    │           ├── ConfirmationOverlay
    │           ├── ModelPickerOverlay
    │           └── ViewSwitcherOverlay
    └── [When toast]
        └── Toast (Box, position: Absolute, bottom/right)
```

---

## 8. 文件结构变更

```
src/LTAI.TUI/
├── Framework/                         # NEW — 框架层
│   ├── Component.cs                   # IComponent 基类 + ContainerComponent
│   ├── Box.cs                         # Flexbox 容器
│   ├── Text.cs                        # 文本组件
│   ├── ScrollBox.cs                   # 可滚动容器
│   ├── TextInput.cs                   # 多行输入组件
│   ├── OverlayManager.cs              # 叠加层管理器
│   ├── Signal.cs                      # Signal/Memo/Effect/Batch
│   ├── RenderLoop.cs                  # 帧循环渲染引擎
│   ├── KeymapEngine.cs                # 键绑定系统 + 模式栈
│   ├── KeyBinding.cs                  # 键绑定定义
│   └── IRenderContext.cs              # 渲染上下文
│
├── Components/                        # NEW — 应用组件
│   ├── AppComponent.cs                # 根组件
│   ├── HomeScreen.cs                  # 首页（Logo + 输入）
│   ├── ChatSession.cs                 # 会话页（消息 + 输入）
│   ├── MessageList.cs                 # 消息列表（ScrollBox）
│   ├── MessageBubble.cs               # 单条消息（User/Assistant）
│   ├── TextPart.cs                    # 文本消息块
│   ├── ToolPart.cs                    # 工具调用块
│   ├── ReasoningPart.cs               # 推理过程块
│   ├── PromptInput.cs                 # 输入组件（TextInput + 历史）
│   ├── StatusBar.cs                   # 底部状态栏
│   ├── Logo.cs                        # Logo 显示
│   └── Toast.cs                       # 通知提示
│
├── Overlays/                          # NEW — 叠加层组件
│   ├── Dialog.cs                      # 对话框容器
│   ├── CommandPicker.cs               # 命令选择器
│   ├── TextInputOverlay.cs            # 文本输入弹窗
│   ├── QuestionOverlay.cs             # 问题表单
│   ├── ConfirmationOverlay.cs         # 确认弹窗
│   ├── ModelPickerOverlay.cs          # 模型选择器
│   └── ViewSwitcher.cs               # 视图切换器
│
├── TuiApp.cs                          # 简化 — 仅保留 DI 编排
├── Program.cs                         # 几乎不变
├── ChatLayout.cs                      # REMOVED — 拆入 Components/
├── Rendering/                         # REPLACED — 功能移至 Framework/
│   ├── ChatRenderer.cs                → 删除
│   ├── MessagePanelRenderer.cs        → MessageList.cs + MessageBubble.cs
│   ├── FooterRenderer.cs              → PromptInput.cs + StatusBar.cs
│   └── SpectreMarkdigRenderer.cs      → 保留（Markdown 渲染）
├── Input/                             # REPLACED — 功能移至 Framework/
│   ├── KeyDispatcher.cs               → 删除（由 KeymapEngine 替代）
│   └── MouseTracker.cs                → 保留（底层读取）
├── Services/                          # 保持不变
│   ├── ThemeService.cs                → 增强为反应式
│   ├── CommandRouter.cs
│   ├── NotificationService.cs
│   └── ...
├── DevUI/                             # 保持不变
└── TextPadView.cs                     # 保持不变（不与 ChatLayout 强耦合）
```

---

## 9. 迁移路线图

### Phase 1: 框架基础（1-2 周）
- [x] `Signal<T>` / `Memo<T>` / `Effect` / `Batch`
- [x] `IComponent` 接口 + `Component` 基类
- [x] `Box` 组件（Flexbox 布局引擎）
- [x] `Text` 组件
- [x] `RenderLoop`（帧循环）
- [x] `RenderContext` 和 `Rect`/`Size`

### Phase 2: 交互系统（1 周）
- [x] `KeymapEngine` + `KeyBinding` + ModeStack
- [x] `OverlayManager` + `Dialog` 组件
- [x] `TextInput` 组件（输入+光标）
- [x] `ScrollBox` 组件

### Phase 3: 应用组件重写（2-3 周）
- [x] `AppComponent`（路由调度）
- [x] `HomeScreen` + `Logo`
- [x] `ChatSession` + `MessageList` + `MessageBubble`
- [x] `PromptInput` + `StatusBar`
- [x] Overlays: `CommandPicker`, `TextInputOverlay`, `QuestionOverlay`, `ConfirmationOverlay`, `ModelPickerOverlay`, `ViewSwitcher`

### Phase 4: 集成与剥离（1 周）
- [x] `TuiApp` 改用新组件系统
- [x] 移除 `ChatLayout`、`ChatRenderer`、`KeyDispatcher`、`FooterRenderer`、`MessagePanelRenderer`
- [x] 连接 `SpectreMarkdigRenderer` 到 `MessageBubble`
- [x] 保留 `MouseTracker` 作为底层输入

### Phase 5: 打磨（持续）
- [x] 100fps 渲染优化
- [x] 暗色/亮色主题适配
- [x] 鼠标交互完善
- [x] 无障碍

---

## 10. 关键设计决策

### 10.1 为什么在 Spectre.Console 上构建 Flexbox？

Spectre.Console 的 IRenderable 是只读快照，无法像 Yoga 那样实现完整的 flexbox 布局。替代方案：

| 方案 | 优点 | 缺点 |
|---|---|---|
| **A. Spectre.Console Layout/Table/Columns** | 零额外依赖 | 布局能力有限，不支持 flexGrow/flexShrink |
| **B. 自建 Flexbox 布局 → 生成 IRenderable** | 完整 Flexbox 语义 | 需要自己计算尺寸和位置 |
| **C. 引入 Yoga .NET 绑定** | 精确匹配 opencode | 额外原生依赖，Windows 兼容性 |

**选择方案 B**：实现简化的 flexbox 子集（flexDirection/grow/shrink/gap/padding/justifyContent/alignItems），用 `Canvas` + `Panel` + `Table` 组合实现。不支持 wrap、basis，但覆盖 90% 使用场景。

### 10.2 信号系统为什么不用 INotifyPropertyChanged？

| 方案 | 优点 | 缺点 |
|---|---|---|
| **A. INotifyPropertyChanged** | .NET 原生，绑定友好 | 无批量更新，无弱订阅，无版本追踪 |
| **B. IObservable / Rx.NET** | 强大组合能力 | 太重，学习曲线高 |
| **C. 自定义 Signal** | 轻量，仿 SolidJS 语义 | 手写，需测试 |

**选择方案 C**：自定义 `Signal<T>` / `Memo<T>` / `Effect` 组合，支持：
- 版本号追踪（用于渲染跳过）
- 批量通知（`Batch.Run`）
- 弱订阅（避免内存泄漏）
- AsyncLocal Effect 追踪（自动依赖收集）

### 10.3 渲染树 vs 虚拟 DOM

不引入虚拟 DOM diff。改用**版本号跳过**：
- `Signal.GlobalVersion` 随信号更新递增
- 每个组件缓存上次渲染的版本号和 IRenderable
- 版本匹配则复用缓存，不调用 Render
- 版本不匹配才重新布局和渲染

这比虚拟 DOM diff 更轻量（O(1) vs O(tree)），但对粒度控制要求更高（需手动声明依赖）。

---

## 11. 使用示例

### 11.1 组件定义

```csharp
public class PromptInput : ContainerComponent
{
    private readonly Signal<string> _text = Signals.Create("");
    private readonly Signal<bool> _focused = Signals.Create(false);
    private readonly KeymapEngine _keymap;
    private readonly Memo<string> _placeholder;

    public PromptInput(KeymapEngine keymap)
    {
        _keymap = keymap;
        _placeholder = Signals.Memo(() =>
            _focused.Value ? "" : "输入消息... (/? 帮助)"
        );

        // 注册局部键绑定
        _keymap.Register(new KeyBinding {
            Mode = "input",
            Key = ConsoleKey.Enter,
            Modifiers = ConsoleModifiers.Shift,
            Description = "换行",
            Execute = () => _text.Value += "\n",
            Enabled = () => _focused.Value,
        });
    }

    protected override IRenderable RenderCore(IRenderContext ctx)
    {
        var text = new Text(_text.Value);
        if (_focused.Value)
            text.Decoration = Decoration.Blink; // 光标

        return new Panel(text) {
            Border = _focused.Value
                ? BoxBorder.Rounded
                : BoxBorder.None
        };
    }
}
```

### 11.2 组件组合

```csharp
public class AppComponent : Box
{
    public AppComponent(KeymapEngine keymap, OverlayManager overlays)
    {
        Direction = FlexDirection.Column;

        var mainContent = new Box { Direction = FlexDirection.Column, FlexGrow = 1 };
        mainContent.Children.Add(new HomeScreen(keymap));

        var statusBar = new StatusBar();
        statusBar.FlexShrink = 0;

        Children.Add(mainContent);
        Children.Add(statusBar);

        // 注册全局键绑定
        keymap.Register(new KeyBinding {
            Mode = "base",
            Key = ConsoleKey.P,
            Modifiers = ConsoleModifiers.Control,
            Execute = () => overlays.Push(new ViewSwitcher(keymap)),
        });
    }
}
```

### 11.3 App 入口

```csharp
// Program.cs — 新增
public static async Task RunTuiAsync(IServiceProvider services)
{
    var rootComponent = services.GetRequiredService<AppComponent>();
    var keymap = services.GetRequiredService<KeymapEngine>();
    var overlays = services.GetRequiredService<OverlayManager>();

    await AnsiConsole.Live(new Canvas(1, 1)) // 占位
        .StartAsync(async ctx =>
        {
            var loop = new RenderLoop(ctx, rootComponent, keymap, overlays);
            await loop.RunAsync();
        });
}
```

---

## 12. 与原系统的兼容策略

| 原有文件 | 处理方式 |
|---|---|
| `ChatLayout.cs` | 删除，功能拆入多个 Component |
| `TuiApp.cs` | 大幅简化，改为组件注册编排 |
| `Program.cs` | 几乎不变，只改 `RunAsync` 调用 |
| `Rendering/ChatRenderer.cs` | 删除 |
| `Rendering/MessagePanelRenderer.cs` | 删除 |
| `Rendering/FooterRenderer.cs` | 删除 |
| `Rendering/SpectreMarkdigRenderer.cs` | **保留**，`MessageBubble` 组件调用 |
| `Rendering/CodeBlockBuffer.cs` | 保留 |
| `Input/KeyDispatcher.cs` | 删除 |
| `Input/MouseTracker.cs` | **保留**，`RenderLoop` 调用读取 |
| `Services/*` | 全部保留 |
| `TextPadView.cs` | 保留 |
| `DevUI/*` | 保留 |

分阶段迁移，逐步替换。第一阶段框架完成后，`ChatLayout` 依然可运行，新组件系统并行开发。

---

## 附录 A: 术语对照

| opencode 术语 | LTAI.TUI 改造后术语 |
|---|---|
| `Signal` | `Signal<T>` |
| `createMemo` | `Signals.Memo<T>` |
| `createEffect` | `Effect` |
| `batch()` | `Batch.Run()` |
| `box` | `Box` |
| `text` | `Text` |
| `scrollbox` | `ScrollBox` |
| `textarea` | `TextInput` |
| `zIndex` | `ZIndex` 属性 |
| `useBindings` | `KeymapEngine.Register` |
| mode stack | 模式栈（PushMode/PopMode） |
| command palette | `CommandPicker` 组件 |
| `DialogProvider` | `OverlayManager` |
| `render()` | `RenderLoop.RunAsync()` |
| `JSX` | C# 代码组合 |
