# LTAI .NET 迁移进度

> 源仓库: https://github.com/ookok/LivingTreeAlAgent (Python ~700模块)  
> 目标仓库: https://github.com/ookok/ltai4net (.NET 10)  
> 更新: 2026-05-19 | 整体迁移完成

## 当前状态

| 指标 | 值 |
|------|-----|
| 项目数 | 26 (19 入 .sln + 7 独立) |
| .cs 文件 | 429 |
| 构建 | **0 错误**, 26 警告 |
| 整体覆盖率 | **~95%** (Python→.NET) |

## 阶段路线

| Phase | 目标 | 状态 |
|-------|------|:----:|
| Phase 1-4 | 基础设施/知识/编排/网络标准化 | ✅ |
| Phase 5-5c | DNA/意识/进化/安全 (30组件) | ✅ |
| Phase 6-6b | 能力层深化 (48组件) | ✅ |
| Phase 7-7d | 执行/网络/核心/观测层深层 | ✅ |
| Phase 8 | Cell/Market/Templates 新项目 | ✅ |
| **Phase 9** | **提示工程 6 件套 (本次补齐)** | ✅ |
| **Phase 10** | **ProviderRegistry 29提供者 + SecretVault (本次补齐)** | ✅ |
| **Phase 11** | **VFS + PublicApis + CapabilityBus (本次补齐)** | ✅ |
| **Phase 12** | **ContextMoE 5层记忆 (本次补齐)** | ✅ |

## 本次会话新增文件一览

| # | 文件 | 项目 | 说明 |
|---|------|------|------|
| 1 | TokenAccountant.cs | LTAI.Web | 四层 Token 边际分配 |
| 2 | RequestBuffer.cs | LTAI.Web | 请求缓冲 + 背压 |
| 3 | CognitionStreamEndpoints.cs | LTAI.Web | 认知流 SSE 端点 |
| 4 | WorkspaceEndpoints.cs | LTAI.Web | 工作空间协作 |
| 5 | ContextBudget.cs | LTAI.Core | 上下文预算管理 |
| 6 | CollectiveIntel.cs | LTAI.Core | 记忆分层 + 蓝图中心 |
| 7 | SessionResilience.cs | LTAI.Core | 会话崩溃恢复 |
| 8 | SeedDevice.cs | LTAI.Core | 角色一键初始化 |
| 9 | FileResolver.cs | LTAI.Core | 统一文件路径解析 |
| 10 | DecoupledExecutor.cs | LTAI.Core | DiLoCo 解耦执行器 |
| 11 | IOOptimizer.cs | LTAI.Core | IO 优化层 |
| 12 | VitalsMonitor.cs | LTAI.Core | 系统生命体征 |
| 13 | AgentQA.cs | LTAI.Core | 蜕变测试 + 黄金追踪 |
| 14 | ProgressiveTrust.cs | LTAI.Core | 渐进用户信任模型 |
| 15 | EntityRegistry.cs | LTAI.Core | 四层跨本体实体注册 |
| 16 | UnifiedRegistry.cs | LTAI.Core | 工具/技能/角色统一注册 |
| 17 | TaskStateManager.cs | LTAI.Core | 任务检查点 + PromptInjector |
| 18 | ProtoSerializer.cs | LTAI.Core | Protobuf 序列化 |
| 19 | JsonUtils.cs | LTAI.Core | 统一 JSON 工具 |
| 20 | ServiceStubs.cs | LTAI.Core | Proto 服务存根 |
| 21 | WhisperSttEngine.cs | LTAI.Core | Whisper STT 引擎 |
| 22 | TtsEngine.cs | LTAI.Core | TTS 语音合成 |
| 23 | FfmpegMediaProcessor.cs | LTAI.Core | FFmpeg 媒体处理 |
| 24 | ProviderRegistry.cs | LTAI.Core | 29 Provider 统一注册 |
| 25 | SecretVault.cs | LTAI.Core | AES-256-GCM 加密密钥库 |
| 26 | ConfigSecurity.cs | LTAI.Core | 配置安全审查 |
| 27 | PromptVersioning.cs | LTAI.TreeLLM | 提示版本管理 + A/B |
| 28 | PromptCoach.cs | LTAI.TreeLLM | L1→L2 元提示教练 |
| 29 | OntoPromptBuilder.cs | LTAI.TreeLLM | 本体感知提示增强 |
| 30 | PromptOptimizer.cs | LTAI.TreeLLM | 角色模板 + 多轮优化 |
| 31 | PromptEngine.cs | LTAI.TreeLLM | DSPy 签名/模块/编译 |
| 32 | ContextMoE.cs | LTAI.TreeLLM | 5层 MoE 上下文记忆 |
| 33 | VfsAdapter.cs | LTAI.Capability | VFS 工具统一注册 |
| 34 | PublicApisResource.cs | LTAI.Capability | 公共 API 资源 |
| 35 | ProcessingFramework.cs | LTAI.Capability | QGIS 处理工具箱 |
| 36 | SelfUpdater.cs | LTAI.Capability | GitHub 自更新 |
| 37 | SmsGateway.cs | LTAI.Capability | SMS 网关 |
| 38 | CapabilityBus.cs | LTAI.AI | 统一能力总线 |
| 39 | EventBusV2.cs | LTAI.Core | 12器官事件总线 |

**本次会话新增 39 个 .cs 文件，项目中 .cs 文件总数从 ~390 增至 429。**

## 未迁移且被替代的模块

| 老项目模块 | .py 数 | 替代方案 |
|-----------|:---:|------|
| client/src (Electron前端) | 467 | MAUI + Blazor |
| HTMX 模板 | 12 | Razor Pages |
| Faiss/HNSW/LanceDB 后端 | 5 | Kernel Memory |
| orjson/protobuf 自研 | 5 | System.Text.Json + Google.Protobuf |
| 自研 token 桶限流 | — | ASP.NET Core Rate Limiter |
| 自研断路器 | — | Polly |
| IProviderEngine | — | IChatClient (M.E.AI) |
| livingtree_pb2 gRPC | 2 | Grpc.Net.Client |
| whisper/edge-tts Python | 3 | Whisper.net + Ollama + FFmpeg |
| numba JIT | 1 | .NET Native AOT / SIMD |
| windsurf gRPC client | 1 | Python特定 |

## 构建状态
- **0 错误**, 26 警告
- 目标框架: .NET 10
- 测试: 5个测试项目 (均未入 .sln)
