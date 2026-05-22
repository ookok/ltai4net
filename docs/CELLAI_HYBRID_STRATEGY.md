# Cell AI 混合策略使用指南

## 概述

Cell AI 混合策略结合了**预训练 ONNX 模型**和**运行时 ML.NET 自训练模型**的优势：

- **冷启动**：预训练模型提供即时可用的意图分类能力
- **持续优化**：运行时数据让模型越来越贴合实际使用场景
- **智能切换**：根据自训练模型质量自动选择最佳模型

## 架构

```
用户查询
  ↓
领域检测 (DetectDomain)
  ↓
答案匹配 (CellAnswerStore)
  ↓
模型选择 (SelectBestModelAndPredict)
  ├─ 自训练模型准确率 >= 75%? → 使用自训练模型
  ├─ 否则有预训练模型? → 使用预训练 ONNX 模型
  └─ 否则回退到自训练模型 (如果可用)
```

## 快速开始

### 1. 配置混合策略

```csharp
// 在 Program.cs 或 Startup 中
services.AddSingleton<CellAIRegistry>(sp =>
{
    var registry = new CellAIRegistry(
        sp.GetRequiredService<CellAnswerStore>(),
        sp.GetRequiredService<SynapticTrainer>(),
        sp.GetRequiredService<SynapticMemory>());
    
    // 配置混合策略
    registry.ConfigureHybridStrategy(
        selfTrainedOverrideThreshold: 0.75f,  // 自训练模型超过此阈值时优先使用
        fallbackToSelfTrained: true);          // 预训练不可用时是否回退
    
    return registry;
});

// 添加预训练模型加载器（自动下载和加载）
services.AddHostedService<PretrainedModelLoader>();
```

### 2. 使用 Cell AI

```csharp
// 在 LivingTreeSystem 或其他服务中
public class MyService
{
    private readonly CellAIRegistry _cellRegistry;
    
    public MyService(CellAIRegistry cellRegistry)
    {
        _cellRegistry = cellRegistry;
    }
    
    public async Task<string> ProcessQueryAsync(string query)
    {
        // 尝试 Cell AI 激活
        var result = _cellRegistry.TryActivateCell(query);
        
        if (result.Activated)
        {
            Console.WriteLine($"Cell AI activated: {result.Domain}");
            Console.WriteLine($"Confidence: {result.Confidence:F2}");
            Console.WriteLine($"Latency: {result.LatencyMs:F1}ms");
            Console.WriteLine($"Model: {result.CellInfo?.ModelType}");
            
            return result.Response;
        }
        
        // 回退到 LLM 或其他处理方式
        return await _llm.GetResponseAsync(query);
    }
}
```

### 3. 监控指标

```csharp
var metrics = _cellRegistry.GetMetrics();

Console.WriteLine($"Total cells: {metrics["total_cells"]}");
Console.WriteLine($"Active cells: {metrics["active_cells"]}");
Console.WriteLine($"Pretrained models: {metrics["pretrained_models"]}");
Console.WriteLine($"Self-trained models: {metrics["self_trained_models"]}");
Console.WriteLine($"Total activations: {metrics["total_activations"]}");

// 查看每个单元格的详细信息
var cells = (Dictionary<string, object>)metrics["cells"];
foreach (var (domain, info) in cells)
{
    Console.WriteLine($"  {domain}:");
    Console.WriteLine($"    State: {info.State}");
    Console.WriteLine($"    Accuracy: {info.Accuracy:F2}");
    Console.WriteLine($"    HasPretrained: {info.HasPretrained}");
    Console.WriteLine($"    HasSelfTrained: {info.HasSelfTrained}");
}
```

## 预训练模型

### 默认模型

| 领域 | 模型来源 | 类别数 | 大小 | 特点 |
|------|---------|--------|------|------|
| code | context4ai/intent-router-onnx | 7 | ~559MB | 支持中英文，代码查询路由 |
| greeting | tanaos/tanaos-intent-classifier-v1 | 12 | ~500MB | 聊天意图分类 |
| general | RunsOnBacon/distilbert-intent-classifier-onnx-int8 | 6 | ~65MB | INT8量化，快速推理 |

### 自定义模型

```csharp
var customModels = new Dictionary<string, OnnxModelConfig>
{
    ["math"] = new OnnxModelConfig
    {
        Domain = "math",
        ModelPath = "/path/to/math-model.onnx",
        Labels = new[] { "arithmetic", "algebra", "calculus", "statistics" },
        MinConfidence = 0.6f
    }
};

await _cellRegistry.InitializePretrainedModelsAsync(customModels);
```

### 手动下载模型

```bash
# 使用 huggingface-cli 下载模型
huggingface-cli download context4ai/intent-router-onnx --local-dir synaptic/pretrained/code

# 或使用 Python
from huggingface_hub import snapshot_download
snapshot_download("context4ai/intent-router-onnx", local_dir="synaptic/pretrained/code")
```

## 模型切换逻辑

### 决策流程

```
自训练模型准确率 >= 75%?
  ├─ 是 → 使用自训练模型（更贴合实际使用场景）
  └─ 否 → 有预训练模型?
           ├─ 是 → 使用预训练模型（冷启动保障）
           └─ 否 → 回退到自训练模型（如果可用）
```

### 调整阈值

```csharp
// 更激进：自训练模型更容易接管
registry.ConfigureHybridStrategy(selfTrainedOverrideThreshold: 0.65f);

// 更保守：预训练模型使用更久
registry.ConfigureHybridStrategy(selfTrainedOverrideThreshold: 0.85f);

// 禁用回退：预训练不可用时不使用自训练模型
registry.ConfigureHybridStrategy(fallbackToSelfTrained: false);
```

## 训练和进化

### 手动训练细胞

```csharp
// 当样本足够时训练细胞
var success = await _cellRegistry.TrainCellAsync("code");

if (success)
{
    Console.WriteLine("Cell trained and activated!");
}
```

### 自动进化循环

`SynapticEvolutionLoop` 后台服务会定期检查：
1. 是否有足够样本训练新细胞
2. 是否需要重新训练现有细胞
3. 是否卸载空闲细胞以释放内存

## 最佳实践

1. **首次部署**：预训练模型提供即时能力，无需等待训练
2. **持续使用**：自训练模型逐渐接管，提供更精准的结果
3. **监控质量**：定期检查 `GetMetrics()` 了解模型状态
4. **调整阈值**：根据实际使用情况调整切换阈值
5. **内存管理**：定期调用 `UnloadIdleCellsAsync()` 释放不常用的模型

## 故障排除

### 预训练模型未加载

```
日志：Pretrained model not available: {Domain}
解决：检查模型文件是否存在，或启用 AutoDownload
```

### 自训练模型未激活

```
日志：Insufficient samples for {Domain}: {Count}/{Min}
解决：收集更多样本，或降低 MinSamplesToTrain
```

### 模型切换不生效

```
日志：Using self-trained model / Using pretrained ONNX model
解决：检查 SelfTrainedOverrideThreshold 设置
```
