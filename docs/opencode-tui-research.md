# opencode TUI 渲染架构研究报告

> 研究目标：分析 opencode (F:\mhzyapp\opencode-dev\opencode-dev) 的 TUI 渲染架构，提取关键模式和 API 设计，供 LTAI.TUI 改造参考。

---

## 1. 技术栈概览

### 1.1 核心依赖

| 包 | 版本 | 角色 |
|---|---|---|
| `@opentui/core` | v0.3.4 | 底层渲染引擎：Ansi 输出、Yoga 布局、cell buffer diff、Tree-sitter 语法高亮 |
| `@opentui/solid` | v0.3.4 | SolidJS 绑定：`render()`、`useTerminalDimensions()`、JSX 元素映射 |
| `@opentui/keymap` | v0.3.4 | 键绑定系统：mode stack、leader key、binding lookup |
| `solid-js` | v1.9.10 | 响应式 UI 框架：Signals、Memos、Effects、Stores |

### 1.2 双端渲染

- **TUI 端** (`packages/tui/`)：SolidJS + OpenTUI，渲染到终端
- **Web 端** (`packages/app/`)：SolidJS + TailwindCSS + Kobalte，渲染到 DOM
- **桌面端** (`packages/desktop/`)：Electron 包裹 Web 端
- **共享 UI** (`packages/ui/`)：187 个组件被 TUI 和 Web 双端共享

---

## 2. 渲染流水线

### 2.1 入口链

```
packages/cli/src/tui.ts
  → packages/tui/src/index.tsx (导出 run)
    → packages/tui/src/app.tsx (run() 函数)
```

### 2.2 run() 函数 (app.tsx:180-337)

```typescript
// Effect-TS 生成器函数
export const run = Effect.fn("Tui.run")(function* (input: TuiInput) {
  // 1. 创建渲染器
  const renderer = yield* Effect.acquireRelease(
    createCliRenderer({
      targetFps: 60,
      exitOnCtrlC: false,
      useKittyKeyboard: {},
      useMouse: !Flag.OPENCODE_DISABLE_MOUSE && input.config.mouse,
    }),
    (r) => Effect.sync(() => r.destroy())
  )

  // 2. 创建 keymap
  const keymap = createDefaultOpenTuiKeymap(renderer)

  // 3. 注册 opencode keymap
  registerOpencodeKeymap(keymap, renderer, input.config)

  // 4. 挂载 SolidJS 应用
  await render(() => <App />, renderer)
})
```

### 2.3 渲染流水线

```
SolidJS JSX 组件树
  → @opentui/solid 编译为 Renderable 对象
    → Yoga Flexbox 布局 (像素→字符网格)
      → Cell Buffer (2D 网格: char, fg, bg, attributes)
        → Diff 计算 (仅输出变化 cell)
          → ANSI Escape Codes → stdout
```

### 2.4 帧循环 (CliRenderer 内部)

- 目标帧率: 60 FPS (`targetFps: 60`)
- 每帧: 轮询终端尺寸 → 处理输入事件 → 刷新差异输出
- 输出策略: 仅发送变化的 cell (字符 + 样式)，非同屏全刷

---

## 3. 组件体系

### 3.1 JSX 元素

| 元素 | 对应组件 | 用途 |
|---|---|---|
| `<box>` | `BoxRenderable` | 通用 flexbox 容器 |
| `<text>` | `TextRenderable` | 文本显示 |
| `<textarea>` | `TextareaRenderable` | 多行文本输入 |
| `<scrollbox>` | `ScrollBoxRenderable` | 可滚动容器 |
| `<span>` | `TextRenderable` | 行内文本样式 |
| `<markdown>` | 自定义渲染 | Markdown 渲染 |

### 3.2 Box 属性

```typescript
<box
  width={number}               // 固定宽度
  height={number}              // 固定高度
  minWidth={number}            // 最小宽度
  minHeight={number}           // 最小高度
  flexGrow={number}            // 弹性增长因子
  flexShrink={number}          // 弹性收缩因子
  flexDirection="row"|"column" // 主轴方向
  gap={number}                 // 子元素间距
  paddingLeft={number}         // 左内边距
  paddingRight={number}        // 右内边距
  paddingTop={number}          // 上内边距
  paddingBottom={number}       // 下内边距
  backgroundColor={RGBA}       // 背景色
  border={["left","right"]}    // 边框边
  borderColor={ColorInput}     // 边框颜色
  justifyContent="space-between"  // 主轴对齐
  alignItems="center"          // 交叉轴对齐
  position="absolute"          // 定位模式
  zIndex={number}              // 层叠顺序
  visible={boolean}            // 可见性
  onMouseDown={fn}             // 鼠标事件
  onMouseUp={fn}               // 鼠标事件
  ref={(r) => handle = r}      // 引用
/>
```

### 3.3 Provider 层级

```typescript
<ExitProvider>
  <EpilogueProvider>
    <ErrorBoundary>
      <TuiPathsProvider>
        <TuiTerminalEnvironmentProvider>
          <TuiStartupProvider>
            <ClipboardProvider>
              <OpencodeKeymapProvider>
                <ArgsProvider>
                  <KVProvider>
                    <ToastProvider>
                      <RouteProvider>
                        <TuiConfigProvider>
                          <PluginRuntimeProvider>
                            <SDKProvider>
                              <ProjectProvider>
                                <SyncProvider>
                                  <SyncProviderV2>
                                    <ThemeProvider>
                                      <LocalProvider>
                                        <PromptStashProvider>
                                          <DialogProvider>
                                            <FrecencyProvider>
                                              <PromptHistoryProvider>
                                                <PromptRefProvider>
                                                  <EditorContextProvider>
                                                    <App />
```

### 3.4 路由组件

```typescript
<box width={dimensions().width} height={dimensions().height} flexDirection="column">
  <Switch>
    <Match when={route.type === "home"}><Home /></Match>
    <Match when={route.type === "session"}><Session /></Match>
  </Switch>
</box>
```

---

## 4. 响应式状态模式 (SolidJS)

### 4.1 Signal

```typescript
const [getter, setter] = createSignal<T>(initial)
// 使用: getter() → T
// 设置: setter(newValue) 或 setter(prev => newValue)
```

### 4.2 Memo

```typescript
const memo = createMemo(() => compute(dep1(), dep2()))
// 自动追踪依赖，只在依赖变化时重算
```

### 4.3 Effect

```typescript
createEffect(() => {
  // 自动追踪所有读取的 signal
  console.log(dep1(), dep2())
  // dep1() 或 dep2() 变化时自动重跑
})
```

### 4.4 Store (深层响应式)

```typescript
const [store, setStore] = createStore({ a: 1, b: { c: 2 } })
// 深度响应式
setStore("b", "c", 3)
```

### 4.5 Batch

```typescript
batch(() => {
  setter1(v1)
  setter2(v2)
  // 合并更新，只触发一次通知
})
```

---

## 5. Keymap 系统

### 5.1 架构分层

```
config/keybind.ts      → 声明式定义 120+ 键绑定的默认值和元数据
keymap.tsx             → 运行时编排：leader key、别名、模式栈
组件级 useBindings()   → 组件声明自己的键绑定（mode/guard/target/run）
```

### 5.2 useBindings 签名

```typescript
useBindings(() => ({
  mode?: string,               // 可选模式过滤器
  target?: () => Renderable,   // 可选焦点目标
  enabled?: () => boolean,     // 可选守卫
  priority?: number,           // 优先级（高者先触发）
  commands?: Command[],        // 命令列表
  bindings?: Binding[],         // 绑定列表
}))
```

### 5.3 模式栈

| 模式 | 说明 |
|---|---|
| `base` | 全局导航 |
| `input` | 文本输入 |
| `leader` | Leader 键按下后 |
| `autocomplete` | 自动补全弹出时 |
| `modal` | 对话框打开时 |

模式栈用 `push/pop` 管理，绑定按当前模式筛选。

### 5.4 Keybind 定义示例

```typescript
app_exit: {
  keys: [KeyStroke({ name: "ctrl+q" })],
  desc: "退出应用",
  group: "应用",
}
// → 映射到命令 "app.exit"
```

---

## 6. Dialog / Overlay 系统

### 6.1 DialogProvider

```typescript
export function DialogProvider(props: ParentProps) {
  return (
    <ctx.Provider value={value}>
      {props.children}
      <box position="absolute" zIndex={3000}>
        <Show when={value.stack.length}>
          <Dialog onClose={() => value.clear()}>
            {value.stack.at(-1)!.element}
          </Dialog>
        </Show>
      </box>
    </ctx.Provider>
  )
}
```

### 6.2 Dialog 组件

```typescript
<box                                         // 全屏遮罩
  width={dimensions().width}
  height={dimensions().height}
  alignItems="center"
  position="absolute" zIndex={3000}
  paddingTop={dimensions().height / 4}
  backgroundColor={RGBA.fromInts(0,0,0,150)}>
  <box width={width()} maxWidth={dimensions().width-2}  // 对话框内容
    backgroundColor={theme.backgroundPanel}>
    {children}
  </box>
</box>
```

### 6.3 使用模式

```typescript
dialog.replace(() => <SomeDialogComponent />)     // 取代当前对话框
dialog.clear()                                     // 关闭所有对话框
dialog.stack                                       // 读取当前栈
dialog.setSize("large")                            // 设置尺寸
```

---

## 7. Theme 系统

### 7.1 主题值代理

```typescript
const theme = new Proxy(values(), {
  get(_target, prop) { return values()[prop] }
})
```

### 7.2 语法高亮

```typescript
const syntax = createSyntaxStyleMemo(
  () => generateSyntax(values())
)
```

使用引用计数的 `SyntaxStyle` 生命周期管理。

### 7.3 主题来源

- 30+ 内置主题 (opencode、dracula、monokai 等)
- 用户自定义 JSON 文件 (`themes/*.json`)
- 终端调色板自动生成 (`renderer.getPalette({size: 16})`)
- `SIGUSR2` 信号重载

---

## 8. 插件系统

### 8.1 Slot 机制

```typescript
<pluginRuntime.Slot name="home_logo" mode="replace">
  <Logo />
</pluginRuntime.Slot>
```

| 模式 | 说明 |
|---|---|
| `replace` | 插件可替换默认内容 |
| `single_winner` | 多插件竞争，只有一个胜出 |

### 8.2 可用 Slot 位置

- `home_logo`、`home_prompt`、`home_prompt_right`、`home_bottom`、`home_footer`
- `session_prompt`、`session_sidebar`、`session_header`
- `app_bottom`、`app`

---

## 9. 关键设计模式总结

| 模式 | opencode 实现 | 适用场景 |
|---|---|---|
| 声明式 UI | SolidJS JSX + 组件组合 | 页面的结构描述 |
| 响应式状态 | Signal/Memo/Effect/Store | 状态变化驱动的 UI 更新 |
| Flexbox 布局 | Yoga 引擎 | 自适应的终端布局 |
| Cell Buffer Diff | OpenTUI Core | 高效的终端输出 |
| 模式栈键绑定 | Keymap + useBindings | 复杂键盘交互 |
| 叠加层 | zIndex + absolute 定位 | 对话框/选择器/弹窗 |
| Provider 注入 | Context 层级 | 全局状态/服务共享 |
| 插件 Slot | Runtime.Slot | 可扩展的 UI 区域 |
| 版本缓存 | Signal 版本号 | 跳过不必要渲染 |
| 批量更新 | batch() | 合并状态变更 |

---

## 10. 与 LTAI.TUI 的关键差异

| 维度 | opencode | LTAI.TUI (当前) |
|---|---|---|
| 渲染方式 | 声明式组件树 | 命令式 Panel 拼接 |
| 状态管理 | 响应式 Signal | 手动字段 + Dirty 标志 |
| 布局 | Yoga Flexbox | Spectre.Console Layout |
| 输入 | Keymap 模式栈 | 双线程 + 大状态机 |
| 对话框 | 叠加层 (zIndex) | 替换 Messages 面板 |
| 更新粒度 | Cell-level diff | 整面板重建 |
| 帧率 | 60fps | 16-50ms 轮询 |
| 组件拆分 | 多小组件 | 单 ChatLayout 1100 行 |
| 主题 | 响应式代理 | 静态 ThemeService |
