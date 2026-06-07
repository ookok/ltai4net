---
name: dotnet-build
description: .NET 项目构建和依赖管理——解决方案结构、项目引用、MAF 子模块、NuGet 配置
license: MIT
allowedTools: [ReadFileContent, SearchContent, Glob, DirectoryTree, ListFiles, RunCommand, FindInCode]
---

# .NET Build & Project Structure

为 LTAI4Net 项目定制的构建和依赖管理技能。

## 项目拓扑

```
LTAI.sln — 根解决方案
├── src/
│   ├── LTAI.Core/          → net10.0, 零外部依赖
│   ├── LTAI.AI/             → 依赖 Core, 引用 MAF Abstractions DLL
│   ├── LTAI.Agent/          → 依赖 Core + AI, 引用 MAF DLLs
│   ├── LTAI.TUI/            → Spectre.Console
│   ├── LTAI.Desktop/        → Avalonia
│   ├── LTAI.Web/            → ASP.NET Minimal API
│   ├── LTAI.Cli/            → NativeAOT, Spectre.Console
│   ├── LTAI.Accelerator/
│   ├── LTAI.Hpo/
│   └── LTAI.Agent.Eia/
├── tests/LTAI.Tests/
├── extern/                  # git submodules
│   ├── agent-framework/     # MAF (Microsoft.Agents.AI)
│   └── durabletask-dotnet/  # DTFx (源码参考，无 ProjectReference)
├── dist/lib/maf/            # 预编译 MAF DLL (通过 build-maf.ps1 生成)
└── aot/rd.xml               # NativeAOT linker 描述符
```

### DI 注册顺序（不可变）

```csharp
services.AddLTAICore();     // 1. 配置、安全、日志
services.AddLTAIAI();       // 2. LLM 路由器、嵌入
services.AddLTAIAgent();    // 3. 10 agents、编排、工具
```

### MAF 子模块

MAF DLL 不在 NuGet 中，而是 git 子模块 + 预编译：

```xml
<!-- 引用预编译 DLL -->
<Reference Include="Microsoft.Agents.AI.Abstractions"
    HintPath="..\..\dist\lib\maf\Microsoft.Agents.AI.Abstractions.dll"
    Condition="Exists('..\..\dist\lib\maf\Microsoft.Agents.AI.Abstractions.dll')" />
```

```bash
./scripts/build-maf.ps1       # 预编译 MAF → dist/lib/maf/
./scripts/dev-setup-submodules.ps1  # 初始化子模块 + sparse-checkout
```

**重要约束：**
- 子模块跟随 main 分支，建议在子模块内 `git checkout <sha>` 锁版本
- `extern/durabletask-dotnet` 仅源码参考，不走 ProjectReference
- MAF 预编译 DLL 在 `dist/lib/maf/`，重建需 `build-maf.ps1`

## 构建命令

```bash
dotnet build LTAI.sln                        # 完整构建
dotnet build src/LTAI.TUI                     # 单项目
dotnet build -warnaserror                     # 零警告检查
dotnet clean && dotnet build LTAI.sln         # 清理重建
```

## NuGet 配置

`NuGet.config` 位于根目录。主要源：
- `nuget.org`
- MAF 不来自 NuGet（本地 DLL）

## 常见问题排查

### 编译错误：找不到 MAF 程序集
```
Solution: 运行 ./scripts/build-maf.ps1，确保 dist/lib/maf/ 存在
```

### SDk 解析错误
项目使用 `net10.0`（.NET 10 preview）。如果 SDK 版本不匹配：
```bash
dotnet --list-sdks              # 检查版本
```

### 子模块问题
```bash
git submodule status                      # 查看子模块状态
git submodule update --init --recursive   # 拉取子模块
```
