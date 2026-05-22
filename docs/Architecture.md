# LTAI L0-L1-L2 架构设计文档

## 🧠 架构全景

L0、L1、L2 并非简单的"模型大小"之分，而是**功能分层与协同进化**的关系。

| 层级 | 定位 | 核心组件 | 职责 | 特点 |
|------|------|----------|------|------|
| **L0** | **感知与记忆基石** | `OnnxEmbeddingBackend`<br>`APIEmbeddingBackend`<br>`DualMemoryStore` | **向量化、检索、记忆存储**<br>将文本转为机器可理解的向量，支撑 RAG 和长期记忆。 | • 决定"系统能记住什么"<br>• 支持本地 ONNX 或云端 API<br>• 语义匹配的基础 |
| **L1** | **本地快脑 (Fast System)** | `LlamaSharpEngine` (RWKV/Qwen)<br>`OnnxSmallLlmEngine`<br>`DreamCycle`<br>`CellAnswerStore` | **日常推理、记忆管理、离线交互**<br>处理 80% 的日常请求，维护上下文，管理 Cells/Graphs。 | • **极低延迟** (<200ms)<br>• **恒定内存** (RWKV O(1))<br>• **隐私安全** (数据不出域)<br>• **无限上下文** (通过记忆检索) |
| **L2** | **云端深脑 (Slow System)** | `ProviderEngine` (DeepSeek/Qwen/GPT)<br>`L2TeachingResult` | **深度推理、复杂任务、教师角色**<br>处理 L1 无法解决的难题，并将推理结果"教"给 L1。 | • **最强推理能力**<br>• **按需调用** (节省成本)<br>• **教学反馈** (提升 L1) |

---

## 🔄 核心数据流与协同机制

### 1. 请求处理流 (Request Flow)

```
用户输入
  ↓
L0: 向量化 Embedding
  ↓
DualMemoryStore: 检索相关记忆
  ↓
L1: 本地模型推理
  ↓
L1L2DuplexRouter 评估
  ├── 简单/有把握 → 直接返回 L1 结果
  └── 复杂/低置信度 → L2: 云端大模型推理
                           ↓
                     返回深度结果
                           ↓
                     提取教学信号 (L2TeachingResult)
                           ↓
                     更新 L1 知识库/规则
```

### 2. 关键关系解析

#### A. L1 ↔ L2：路由与教学 (Routing & Teaching)
*   **守门人 (L1)**：`L1L2DuplexRouter` 是核心枢纽。L1 首先尝试回答，并自我评估质量 (`EstimateResponseQuality`)。
*   **升级条件**：
    *   检测到复杂关键词（分析、代码、数学）。
    *   L1 生成结果质量低（重复、过短、逻辑混乱）。
    *   用户显式要求（"详细解释"、"深度分析"）。
*   **教学闭环**：L2 不仅回答问题，还输出 `ReasoningSteps` 和 `KeyConcepts`。系统通过 `TeachingRuleExtractor` 将这些提取为规则，存入 `CellAnswerStore`。**下次 L1 遇到类似问题时，可直接命中规则，无需再问 L2。**

#### B. L0 ↔ L1：记忆与检索 (Memory & RAG)
*   **记忆存储**：所有 L1/L2 的交互历史，经 L0 向量化后存入 `DualMemoryStore`。
*   **按需召回**：L1 不需要把所有历史塞进 Prompt。当用户提到过去的事，L0 负责检索最相关的片段，注入 L1 的上下文。
*   **无限上下文**：L1 (RWKV) 的 O(1) 特性 + L0 的精准检索 = **感官上的无限记忆**。

#### C. L1 的自我进化 (DreamCycle)
*   **后台巩固**：`DreamCycle` 定时运行，将 L1 的短期记忆（Episodic）压缩为长期规则（Semantic）。
*   **领域发现**：`DomainDiscoveryService` 自动发现新领域，加载对应的 `.graphpackage` 或 `.cellpackage`，扩展 L1 的能力边界。

---

## 🚀 架构优势总结

1.  **成本极低**：80% 请求由本地 L1 处理，L2 仅在必要时调用，API 费用降低 70-80%。
2.  **速度极快**：日常交互无网络延迟，L1 响应 <200ms。
3.  **隐私安全**：敏感数据在本地处理，不上传云端。
4.  **无限上下文**：RWKV 的恒定内存 + RAG 检索，打破 Transformer 的窗口限制。
5.  **持续进化**：L2 教 L1，L1 越用越聪明，减少未来对 L2 的依赖。
6.  **离线可用**：即使断网，L1 + 本地记忆仍能提供高质量服务。

---

## 📦 物理部署示意

```
[ 用户设备 (PC/手机/边缘服务器) ]
├── L0: Embedding Model (ONNX / API)
├── L1: RWKV-7 1.5B/2.9B (GGUF) + Memory DB
└── Router: L1L2DuplexRouter (决策逻辑)
       │
       ▼ (仅 20% 复杂请求)
[ 云端 API (DeepSeek / Qwen / GPT) ]
└── L2: Large Model (深度推理 + 教学)
```

---

## 📂 关键代码文件索引

| 组件 | 文件路径 |
|------|----------|
| **L0 Embedding** | `src/LTAI.Vector/Embedding/OnnxEmbeddingBackend.cs` |
| **L0 API** | `src/LTAI.Vector/Embedding/APIEmbeddingBackend.cs` |
| **L0 Memory** | `src/LTAI.AI/Governors/DualMemoryStore.cs` |
| **L1 GGUF Engine** | `src/LTAI.AI/Governors/LlamaSharpEngine.cs` |
| **L1 ONNX Engine** | `src/LTAI.AI/Governors/OnnxSmallLlmEngine.cs` |
| **L1 Router** | `src/LTAI.AI/Governors/L1L2DuplexRouter.cs` |
| **L1 Memory Cycle** | `src/LTAI.AI/Governors/DreamCycle.cs` |
| **L2 Provider** | `src/LTAI.AI/Providers/ProviderEngine.cs` |
| **Bootstrap** | `src/LTAI.AI/Governors/LocalLlmBootstrapService.cs` |
| **Model Registry** | `src/LTAI.Core/Governors/RwkvModelRegistry.cs` |
| **Setup Wizard** | `src/LTAI.Core/Setup/InteractiveSetupWizard.cs` |

---

*最后更新：2026-05-21*
