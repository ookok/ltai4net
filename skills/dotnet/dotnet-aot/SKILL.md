---
name: dotnet-aot
description: .NET NativeAOT 发布——修剪配置、IL 链接器警告、rd.xml 描述符、AOT 兼容性
license: MIT
allowedTools: [ReadFileContent, SearchContent, Glob, FindInCode, RunCommand]
---

# .NET NativeAOT Publishing

LTAI4Net 使用 NativeAOT 发布 `LTAI.Cli` 和 `LTAI.Web` 两个项目。

## 项目配置

```xml
<!-- LTAI.Cli.csproj / LTAI.Web.csproj -->
<PublishAot>true</PublishAot>
<OptimizationPreference>Size</OptimizationPreference>
<IlcInstructionSet>native</IlcInstructionSet>
<IlcGenerateStackTraceData>false</IlcGenerateStackTraceData>
<IlcDisableUnhandledExceptionExperience>true</IlcDisableUnhandledExceptionExperience>
<IlcTreatLinkingErrorsAsWarnings>true</IlcTreatLinkingErrorsAsWarnings>
```

### IlcTreatLinkingErrorsAsWarnings

这个项目必须使用 `IlcTreatLinkingErrorsAsWarnings=true`，因为：
1. **YamlDotNet 反射** — MAF 工作流通过 YAML 定义，YamlDotNet 使用大量反射
2. **DurableTask 反射** — DTFx 使用 `Type.GetField`/`Type.GetMethod` 等
3. **JSON 序列化** — 大量 `JsonSerializer.Deserialize<T>()` 调用，T 在运行时才确定
4. **FunctionResultContent** — MAF 的 `FunctionResultContent.Result` 使用 `object?`

## AOT 兼容性标记

```csharp
// 对于根本上与 AOT 不兼容的组件，标记 [RequiresDynamicCode]
[DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SomeType))]
[RequiresDynamicCode("Uses Roslyn scripting which requires dynamic code")]
public sealed class CSharpScriptTool { ... }
```

## rd.xml 链接器描述符

所有反射根在 `aot/rd.xml` 中声明：

```xml
<Directives>
  <!-- DI 类型 -->
  <Type Name="LTAI.Agent.Workflows.YAMLWorkflowRegistry" Dynamic="Required All" />

  <!-- MAF 程序集（整程序集保留） -->
  <Assembly Name="Microsoft.Agents.AI" Dynamic="Required All" />

  <!-- 知识图谱模型 -->
  <Type Name="LTAI.Agent.Vector.KgStore" Dynamic="Required All" />

  <!-- 工具注册表 -->
  <Type Name="LTAI.AI.ToolRegistry" Dynamic="Required All" />

  <!-- JSON 反序列化类型 -->
  <Type Name="System.Text.Json.JsonSerializer" Dynamic="Required All" />
</Directives>
```

### rd.xml 编写规则

| 场景 | rd.xml 配置 |
|------|-------------|
| DI 注册的服务 | `Dynamic="Required All"` |
| MAF 代理类型 | `Dynamic="Required All"` |
| JSON 反序列化的模型 | `Dynamic="Required Public"` | 
| 泛型类型实例化 | 明确列出封闭泛型类型 |
| 整程序集保留 | `<Assembly Name="Xxx" Dynamic="Required All" />` |

## 支持的警告抑制

```xml
<NoWarn>$(NoWarn);IL2026;IL3050;IL2046;IL2091;IL2104</NoWarn>
```

| 警告 | 原因 | 处理方式 |
|------|------|----------|
| IL2026 | `RequiresDynamicCode` 调用链 | rd.xml 或 `[RequiresDynamicCode]` |
| IL3050 | AOT+反射组合 | rd.xml 保留 |
| IL2046 | 基类/接口 AOT 冲突 | rd.xml 补充 |
| IL2091 | 泛型 AOT 分析 | 闭包 rd.xml |
| IL2104 | 构造函数反射 | rd.xml 声明 |

## 发布命令

```bash
# Windows
dotnet publish src/LTAI.Cli -c Release -r win-x64

# Linux
dotnet publish src/LTAI.Cli -c Release -r linux-x64

# macOS
dotnet publish src/LTAI.Cli -c Release -r osx-arm64
```

## 常见问题

### ILc 链接错误而不是警告
```
Solution: 确认 IlcTreatLinkingErrorsAsWarnings=true 已设置
```

### JSON 反序列化在 AOT 下返回空对象
```
Solution: 在 rd.xml 中添加对应 model 类型的 Dynamic 保留
```

### MAF 工作流在执行时失败
```
Solution: 确保 Microsoft.Agents.AI 整程序集在 rd.xml 中保留
```

### Func<> / Expression<> 动态编译失败
```
Solution: .NET 10 NativeAOT 不支持 Expression.Compile() 动态编译。
使用 source-generated 表达式或静态委托替代。
```
