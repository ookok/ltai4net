# LTAI 细胞 AI 进化蓝图

## 基座模型选型

### 推荐基座：RWKV-7-G1-0.4B-Q4 (250MB) + SmolLM2-135M-ONNX (100MB) 双引擎

| 模型 | 存储 | 内存 | 推理速度(CPU) | 优势 |
|------|------|------|-------------|------|
| **RWKV-7-0.4B-Q4** | 250MB | 800MB | 45 tok/s | O(n)线性注意力，无KV缓存增长，恒定内存 |
| **SmolLM2-135M-ONNX** | 100MB | 400MB | 120 tok/s | Transformer，极致轻量，ONNX原生 |
| **SmolLM2-360M-ONNX** | 280MB | 1GB | 60 tok/s | 360M参数，质量接近0.5B，已在注册表 |
| **Qwen2.5-0.5B-ONNX** | 400MB | 1.5GB | 40 tok/s | Qwen系列，中文原生支持，已在注册表 |

### 选型原则

```
设备分级:
  IoT/MCU (<256MB RAM)  → RWKV-7-0.1B (未来) 或 纯分类器
  手机/平板 (2-4GB RAM) → RWKV-7-0.4B + SmolLM2-135M 双引擎
  笔记本 (8GB+ RAM)     → Qwen2.5-1.5B + RWKV-7-1.5B
  桌面/工作站           → Qwen2.5-7B + SmolLM2-1.7B

核心策略: L1小模型完成80%请求，L2云端大模型只做深度推理补充(20%)
         自适应深度控制器(已实现)自动判断是否需要L2
```

---

## 六项前沿创新 (含实现路径)

### 1. 推测解码 (Speculative Decoding) — 2-3x加速

**论文**: Leviathan 2023 (DeepMind), Medusa 2024, Eagle 2024  
**原理**: 小draft模型生成5-8个候选token → 大target模型一次验证 → 接受/拒绝  
**LTAI实现**:

```
SmolLM2-135M (draft, 120 tok/s)
    ↓ 生成8个候选token
RWKV-7-0.4B / Qwen2.5-0.5B (target)
    ↓ 一次forward验证全部8个token
接受率 ~75% → 有效速度 = 0.75 × 8 × target_speed ≈ 3x加速
```

**新增文件**: `LTAI.AI/Acceleration/SpeculativeDecoder.cs`
**改动量**: ~200行，无外部依赖，纯ONNX tensor操作

---

### 2. SPIN 自对弈进化 — 零人工数据

**论文**: SPIN (Chen 2024, UCLA), Self-Rewarding LMs (Meta 2024)  
**原理**: 模型跟自己对弈 → 生成训练数据 → 自我标注 → 自我改进  
**LTAI实现**:

```
SynapticMemory中未标注样本
    ↓ L1生成回答 (player)
StructuredReflectionEngine 评分 (judge)  
    ↓ reward > 0.7 → 加入训练集
TieredLoraManager.TrainTier()
    ↓ LoRA增量更新
模型能力螺旋上升 (每一轮都比上一轮强)
```

**新增文件**: `LTAI.AI/Governors/SpinSelfPlayLoop.cs`  
**改动量**: ~150行，复用已有SynapticMemory + ReflectionEngine

---

### 3. 联邦蒸馏 + 差分隐私 — 设备间知识共享不泄露

**论文**: Federated Distillation (Jeong 2018), DP-FedAvg (McMahan 2017)  
**原理**: 设备本地训练 → 只上传logits/soft labels(非权重) → 服务器聚合 → 分发蒸馏  
**LTAI实现**:

```
设备A: LoRA训练 → 导出 soft labels → +Laplace噪声(DP ε=8)
设备B: LoRA训练 → 导出 soft labels → +Laplace噪声
    ↓
FederatedLearningService (已有骨架)
    ↓ KL蒸馏聚合
CrossLevelDistiller (已实现)
    ↓
全局模型更新 → 分发回设备
```

**改动量**: 增强 `FederatedLearningService.cs`，接入 `CrossLevelDistiller`

---

### 4. 混合专家动态路由 — 不同查询用不同"脑区"

**论文**: Switch Transformer (Fedus 2021), DeepSeek-MoE (2024), RouteLLM (2024)  
**原理**: 多个小型专家网络，每个专精一个领域。路由器动态选择top-k专家  
**LTAI实现**:

```
自适应深度控制器 (已实现)
    ↓ Decide(query) → Tier + 领域
专家路由器 (新增)
    ├── CodeExpert (代码)     → SmolLM2-135M-code
    ├── MathExpert (数学)     → 验证管线(工具调用)
    ├── ChatExpert (对话)     → Qwen2.5-0.5B
    ├── ReasoningExpert (推理) → RWKV-7 + MCTS
    └── EIAExpert (环评)      → 知识库检索
    ↓ Top-2专家激活 → 结果融合
```

**新增文件**: `LTAI.AI/Governors/MoERouter.cs`  
**改动量**: ~120行，接入已有 AdaptiveDepthController + TieredLoraManager

---

### 5. 渐进式层冻结 (Progressive Layer Freezing) — 训练加速5x

**论文**: Freeze-OUT (Brock 2017), Progressive Freezing (Howard 2018)  
**原理**: 训练时逐层冻结参数。前几轮全量训练 → 逐步冻结底层 → 最终只训练顶层LoRA  
**LTAI实现**:

```
轮次1: 所有层可训 (warmup)
轮次2: 冻结Layer 0-1 (底层特征已稳定)
轮次3: 冻结Layer 0-3
轮次4+: 只训练Layer 4-5 + LoRA (顶层语义)
训练速度: 全量 → 50% → 25% → 10%  (逐轮加速)
```

**改动量**: 增强 `IntentClassifierNetwork.Train()`，添加层冻结参数

---

### 6. 模型汤 (Model Soup) — 零成本多模型融合

**论文**: Model Soups (Wortsman 2022, Meta)  
**原理**: 多个fine-tuned模型的权重取平均(不是推理结果平均)，零额外推理成本  
**LTAI实现**:

```
TieredLoraManager中不同设备/时间训练的LoRA checkpoint:
  lora_A.weights.json  (设备A, 100样本, rank-4)
  lora_B.weights.json  (设备B, 80样本, rank-4)
  lora_C.weights.json  (设备C, 150样本, rank-8)
    ↓ 统一rank后平均A矩阵和B矩阵
lora_soup = (A_A + A_B + A_C) / 3   (权重平均)
    ↓
性能通常 > 任何单个模型
```

**改动量**: `LoraLayer.cs` 添加 `AverageWith(LoraLayer other)` 方法，~30行

---

## 自动进化流水线 (端到端)

```
┌─────────────────────────────────────────────────────┐
│                CELLULAR AI PIPE                      │
├─────────────────────────────────────────────────────┤
│                                                       │
│ ① 交互采集                                           │
│   用户请求 → L1快速响应(LoRA) → 存入SynapticMemory     │
│   LivingTreeSystem.ProcessTypedAsync() 每请求采集      │
│                                                       │
│ ② 质量筛选 (自动)                                     │
│   StructuredReflectionEngine 评分 → reward > 0.7保留  │
│   PACE追踪 → 识别高价值样本(引发显著参数变化的查询)      │
│   CoEchoDetector → 过滤重复/低信息量样本               │
│                                                       │
│ ③ 自对弈数据增强 (自动, SPIN)                         │
│   SpinSelfPlayLoop: L1 vs L1 对弈 → 生成合成数据       │
│   对抗样本生成: 同一query用不同tier回答 → 对比学习      │
│                                                       │
│ ④ 分级训练 (自动)                                     │
│   TieredLoraManager: 按tier分配样本 → 独立LoRA训练     │
│   渐进层冻结: warmup → 逐轮冻结底层 → 只训顶层LoRA     │
│   推测解码验证: draft模型生成 → target验证 → 接受/拒绝  │
│                                                       │
│ ⑤ 联邦聚合 (自动)                                     │
│   设备导出soft labels(+DP噪声) → FederatedLearning     │
│   → CrossLevelDistiller 蒸馏聚合 → 全局模型            │
│   → 模型汤(权重平均多个checkpoint) → 零成本提升         │
│                                                       │
│ ⑥ 热更新 (自动, 无感)                                 │
│   新模型就绪 → SynapticInference.HotReload()           │
│   → InferenceSession原子替换 → <10ms停机               │
│   → 自适应深度控制器自动切换tier → 用户无感知           │
│                                                       │
│ ⑦ 能力验证 (自动)                                     │
│   保留集评估: 旧模型 vs 新模型 accuracy对比             │
│   退化检测: 新模型accuracy < 旧模型*0.95 → 自动回滚     │
│   SynapticPlasticity.degradation_score → 触发告警      │
│                                                       │
│ ⑧ 模型分发 (自动)                                     │
│   细胞模型打包: CellPackageManager.Compress()          │
│   → ONNX INT8量化 → 生成 cell_package_v{N}.tar.gz     │
│   → P2P分发给邻居节点 (A2aP2pBridge已有)               │
└─────────────────────────────────────────────────────┘
```

---

## 实现优先级 (分4个Sprint)

### Sprint 1: 推测解码 (价值最高, 2-3x加速)
- `SpeculativeDecoder.cs` (~200行)
- 改动 `OnnxSmallLlmEngine` 接入 draft model
- CPU上从40 tok/s → 100+ tok/s

### Sprint 2: 自对弈进化 + 渐进层冻结 (自动化核心)
- `SpinSelfPlayLoop.cs` (~150行)
- 增强 `IntentClassifierNetwork.Train()` 层冻结
- 训练加速5x，数据零人工

### Sprint 3: MoE路由 + 模型汤 (质量提升)
- `MoERouter.cs` (~120行)
- `LoraLayer.AverageWith()` (~30行)
- 查询到专家精准匹配，多模型融合

### Sprint 4: 联邦蒸馏 (规模化)
- 增强 `FederatedLearningService.cs` + DP噪声
- 设备间知识共享，边云协同

---

## 基座模型补充建议

当前注册表缺少的关键模型:
1. **SmolLM2-135M-ONNX** — 100MB, 极致轻量, 做draft model
2. **MobileLLM-125M** — Meta 2024, deep-and-thin架构, 质量超越同参数量模型
3. **Qwen2.5-0.5B-Instruct-ONNX** — 中文指令微调, 替代基础版
4. **Jina-Reader-LM-v2** — 284M参数可从URL提取干净文本, 做RAG前端
5. **gte-Qwen2-1.5B-instruct** — 目前最强的1.5B开源嵌入模型

### 不推荐
- ❌ BitNet b1.58 — 生态不成熟, ONNX支持不完整
- ❌ DeepSeek-V2-Lite — 2.4B MoE对边缘设备仍过重
- ❌ Llama-3.2-1B — 质量不如同量级Qwen2.5, 且不支持中文
