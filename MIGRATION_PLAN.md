# LTAI .NET 迁移计划

## 技术选型

| 层 | 技术 | 选型依据 |
|----|------|----------|
| **运行时** | .NET 10 (preview) | 最高性能，原生AOT支持 |
| **LLM网关** | 自研 ProviderEngine (OpenAI-compatible API) | 零依赖，28家厂商兼容 |
| **Agent框架** | 自研 CognitiveMesh + LayerGovernor 架构 | 仿生神经系统，10层协同；比 Semantic Kernel 更轻量 |
| **Web框架** | ASP.NET Core Minimal API | 微软生态标配 |
| **浏览器** | PuppeteerSharp | .NET 生态最成熟的 Chrome 控制库 |
| **HTML解析** | HtmlAgilityPack | 轻量，XPath/CSS选择器 |
| **DB: 结构化** | Microsoft.Data.Sqlite + FTS5 | 零配置，全文搜索 |
| **DB: 向量** | 内存 Cosine TopK → 后续接入 Qdrant/Milvus SDK | 阶段一用内存方案快速验证 |
| **嵌入模型** | 当前字符哈希→后续 ONNX Runtime + MiniLM | 快速启动，后续上真模型 |
| **序列化** | System.Text.Json | 原生，AOT友好 |
| **gRPC** | Grpc.Net.Client + Google.Protobuf | P2P通信 |
| **测试** | xUnit + Microsoft.NET.Test.Sdk | .NET 生态标配 |

## 模块映射 (Python → .NET)

```
Python (LivingTreeAlAgent)          →  .NET (ltai4net)
─────────────────────────────────────────────────────────
livingtree/core/                    →  LTAI.Core
livingtree/treellm/core.py          →  LTAI.AI/Providers/ProviderEngine.cs
livingtree/execution/               →  LTAI.AI/Governors/*.cs (10个Governor)
livingtree/api/ + htmx_web/         →  LTAI.Web
livingtree/knowledge/vector_store   →  LTAI.Vector/VectorStore.cs
livingtree/knowledge/document_store →  LTAI.Vector/Knowledge/DocumentStore.cs
livingtree/knowledge/knowledge_base →  LTAI.Vector/Knowledge/KnowledgeBase.cs
livingtree/capability/browser_agent →  LTAI.Browser/BrowserAgent.cs
livingtree/capability/browser_tools →  LTAI.Host/Program.cs (ToolRegistry注册)
livingtree/capability/doc_engine    →  LTAI.Document/UniversalFileParser.cs
livingtree/network/p2p_node         →  LTAI.Network/P2PNode.cs
```

## 当前实现状态

### 已完成 (7个项目，44个.cs文件，34个测试)

| 项目 | 文件数 | 状态 | 测试 |
|------|--------|------|------|
| LTAI.Core | 11 | 完整 (接口/模型/消息总线/工具注册/配置) | - |
| LTAI.AI | 13 | 完整 (10个Governor + LLM Engine + 守护进程) | - |
| LTAI.Web | 3 | 完整 (REST API + 速率限制) | - |
| LTAI.Vector | 8 | 完整 (IVectorStore *IEmbeddingBackend *DocumentStore *KnowledgeBase) | 7+10 |
| LTAI.Browser | 4 | 完整 (Puppeteer浏览器 + HtmlAgilityPack自适应提取) | 6 |
| LTAI.Document | 4 | 完整 (UniversalFileParser + 4种解析器) | 10 |
| LTAI.Network | 4 | 基本 (P2P Node + ServiceDiscovery) | 4 |
| LTAI.Host | 2 | 完整 (Program.cs + appsettings.json + 5工具注册) | - |

### 待集成

- [ ] DocumentStore FTS5虚拟表查询修复 (`SQLite Error: no such column: f`)
- [ ] IVectorStore in ContextGovernor 已验证编译，待端到端测试
- [ ] LLM工具调用链: CapabilityGovernor ← IToolRegistry ← browser/web_fetch/doc_parse/vector_search
- [ ] 真嵌入模型: ONNX Runtime × MiniLM-L6-v2 替代字符哈希
- [ ] 向量存储: Qdrant/Milvus SDK 替代内存 Cosine
- [ ] 会话持久化: SQLite session memory
- [ ] RAG流水线端到端验证

## 架构原则

1. **零循环依赖**: 依赖方向 ↓ only
   ```
   LTAI.Host → LTAI.Web → LTAI.AI → LTAI.Core
                                    → LTAI.Vector
                  LTAI.Browser → LTAI.Core
                  LTAI.Document → LTAI.Core
                  LTAI.Network → LTAI.Core
   ```

2. **接口先行**: 所有能力通过接口暴露 (ILayerGovernor, IToolRegistry, IVectorStore, IBrowserAgent, IDocumentParser, IP2PNode)

3. **DI驱动**: 通过 Microsoft.Extensions.DependencyInjection 组装，无静态单例

4. **CognitiveMesh 消息路由**: 10个Governor通过 Handshake 消息协作，模仿生物神经系统信号传递

5. **工具注册透明**: LLM看到工具列表 → 自主选择调用 → CapabilityGovernor分发执行

## 下一步 (优先级排序)

### P0 - 修复
1. 修复 DocumentStore FTS5 查询 "no such column: f" 错误
2. 确保 Knowledge 测试全部通过 (当前 17/17 通过, 2个FTS相关因SQL问题失败)

### P1 - 集成
3. ContextGovernor 知识预加载端到端验证 (注入种子文档 → Chat API → 验证检索上下文)
4. 宿主应用启动入口测试

### P2 - 增强
5. ONNX Runtime 嵌入模型替换
6. Qdrant 向量后端
7. 会话持久化
8. 前端 Dashboard (HTMX + Tailwind)

## 参考

- C# AI Agent 技术选型参考: https://blog.raikay.cn/posts/25/csharp-ai-gent/
- Python 源码: D:\mhzyapp\LivingTreeAlAgent
- .NET 源码: D:\mhzyapp\ltai4net
