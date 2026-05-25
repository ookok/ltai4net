namespace LTAI.AI.Governors;

public static class Gdn2OnnxOperators
{
    public static readonly string[] RequiredOps =
    [
        "MatMul",
        "Add",
        "Mul",
        "Sigmoid",
        "Softplus",
        "Exp",
        "Log",
        "Div",
        "Sub",
        "Reshape",
        "Transpose",
        "Concat",
        "Where",
        "ReduceMean",
        "ReduceSum",
        "LayerNormalization",
        "Conv"
    ];

    public static readonly string[] OptionalOps =
    [
        "Scan",
        "Loop",
        "Cast"
    ];

    public static readonly (string op, string gdn2Use)[] OpTable =
    [
        ("MatMul",    "q/k/v 投影 (Linear 权重) + 外积 k_t ⊗ k_t^T / k_t ⊗ v_t^T"),
        ("Add",       "Linear bias + 残差连接"),
        ("Mul",       "SiLU(Sigmoid×x) + 逐元素门控 b_t⊙k_t / w_t⊙v_t"),
        ("Sigmoid",   "b_t (erase gate) + w_t (write gate) + SiLU 激活"),
        ("Softplus",  "Dt = Softplus(f_proj + dt_bias) 步长计算"),
        ("Exp",       "A_log exp 转换为衰减率; Softmax (备选)"),
        ("Log",       "A_log 对数参数初始化"),
        ("Div",       "RMSNorm 归一化"),
        ("Sub",       "I - k(b⊙k)^T 恒等矩阵减秩一修正"),
        ("Reshape",   "multi-head 切分/合并 (B,T,H,D) ↔ (B,T,HD)"),
        ("Transpose", "注意力头维度转置"),
        ("Concat",    "多输出拼接"),
        ("Where",     "分组值注意力 GVA 的掩码路由"),
        ("ReduceMean","LayerNorm 统计量"),
        ("ReduceSum", "RMSNorm 统计量"),
        ("LayerNormalization", "o_norm 输出门控归一化"),
        ("Conv",      "q/k/v 短卷积 (局部感受野，kernel 4)"),
        ("Scan",      "chunkwise 循环展开 (可选，批量推理优化)"),
        ("Loop",      "fused_recurrent 逐 token 迭代 (可选，单 token 解码)"),
        ("Cast",      "fp16→fp32 精度转换 (数值稳定)"),
    ];

    public static bool IsOnnxSupported()
    {
        try
        {
            var providers = Microsoft.ML.OnnxRuntime.OrtEnv.Instance().GetAvailableProviders();
            return providers.Any();
        }
        catch { return false; }
    }

    public static string GetExportStrategy()
    {
        return string.Join('\n',
            "1. 将 q_proj/k_proj/v_proj 导出为 Linear 节点 (MatMul+Add)",
            "2. 将 f_proj/b_proj/w_proj/g_proj 导出为相同的 Linear 节点",
            "3. 核心循环用 Scan 算子: body=一个时间步的 S_{t-1} → S_t 更新",
            "4. Scan body 内部: Sigmoid→Mul→MatMul→Sub→Mul→Add",
            "5. 短卷积用 Conv(kernel=4, groups=d_k, depthwise)",
            "6. 输出用 LayerNormalization 算子",
            "7. 最终导出 opset≥18 (支持 LayerNormalization v17+)"
        );
    }
}
