using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace LTAI.Core.Governors;

public enum ModelLayer { L0, L1, L2 }

public record LocalModelInfo(
    string Version,
    string Name,
    string Url,
    string MirrorUrl,
    string Sha256,
    long RecommendedMemoryMB,
    long DiskSizeMB,
    string Description,
    ModelLayer Layer,
    string EngineType = "gguf");

public static class LocalModelRegistry
{
    public static readonly IReadOnlyList<LocalModelInfo> AvailableModels = new[]
    {
        // ==================== L0: Embedding Models (ONNX) ====================
        new LocalModelInfo(
            Version: "bge-large-zh-v1.5-onnx",
            Name: "BGE-Large-ZH-v1.5 (ONNX) - 中文嵌入",
            Url: "https://huggingface.co/BAAI/bge-large-zh-v1.5/resolve/main/onnx/model.onnx",
            MirrorUrl: "https://hf-mirror.com/BAAI/bge-large-zh-v1.5/resolve/main/onnx/model.onnx",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 2048,
            DiskSizeMB: 1200,
            Description: "智源 BGE 中文大向量模型，MTEB 中文榜单前列。适合语义搜索、RAG。",
            Layer: ModelLayer.L0,
            EngineType: "onnx"),

        new LocalModelInfo(
            Version: "bge-small-zh-v1.5-onnx",
            Name: "BGE-Small-ZH-v1.5 (ONNX) - 轻量中文嵌入",
            Url: "https://huggingface.co/BAAI/bge-small-zh-v1.5/resolve/main/onnx/model.onnx",
            MirrorUrl: "https://hf-mirror.com/BAAI/bge-small-zh-v1.5/resolve/main/onnx/model.onnx",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 1024,
            DiskSizeMB: 350,
            Description: "BGE 轻量中文版，适合内存受限场景。",
            Layer: ModelLayer.L0,
            EngineType: "onnx"),

        new LocalModelInfo(
            Version: "bge-m3-onnx",
            Name: "BGE-M3 (ONNX) - 多语言嵌入",
            Url: "https://huggingface.co/BAAI/bge-m3/resolve/main/onnx/model.onnx",
            MirrorUrl: "https://hf-mirror.com/BAAI/bge-m3/resolve/main/onnx/model.onnx",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 4096,
            DiskSizeMB: 2200,
            Description: "BGE-M3 多语言模型，支持 100+ 语言，稠密+稀疏+多向量检索。",
            Layer: ModelLayer.L0,
            EngineType: "onnx"),

        // ==================== L1: Fast Models (GGUF) ====================
        new LocalModelInfo(
            Version: "rwkv7-g1-0.4b-q4",
            Name: "RWKV-7 G1 0.4B (Q4_K_M) - 极轻量",
            Url: "https://huggingface.co/Mungert/rwkv7-0.4B-g1-GGUF/resolve/main/rwkv7-0.4b-g1-q4_k_m.gguf",
            MirrorUrl: "https://hf-mirror.com/Mungert/rwkv7-0.4B-g1-GGUF/resolve/main/rwkv7-0.4b-g1-q4_k_m.gguf",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 2048,
            DiskSizeMB: 250,
            Description: "极轻量模型，适合 2GB+ 内存设备或嵌入式场景。仅基础对话。",
            Layer: ModelLayer.L1,
            EngineType: "gguf"),

        new LocalModelInfo(
            Version: "rwkv7-g1-1.5b-q4",
            Name: "RWKV-7 G1 1.5B (Q4_K_M) - 中文优化",
            Url: "https://huggingface.co/zhiyuan8/RWKV-v7-1.5B-G1-GGUF/resolve/main/rwkv7-1.5b-g1-q4_k_m.gguf",
            MirrorUrl: "https://hf-mirror.com/zhiyuan8/RWKV-v7-1.5B-G1-GGUF/resolve/main/rwkv7-1.5b-g1-q4_k_m.gguf",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 4096,
            DiskSizeMB: 990,
            Description: "轻量级本地模型，World 词表支持 100+ 语言。C-Eval 42.7%，适合日常对话。",
            Layer: ModelLayer.L1,
            EngineType: "gguf"),

        new LocalModelInfo(
            Version: "rwkv7-g1-2.9b-q4",
            Name: "RWKV-7 G1 2.9B (Q4_K_M) - 中文增强",
            Url: "https://huggingface.co/zhiyuan8/RWKV-v7-2.9B-G1-GGUF/resolve/main/rwkv7-2.9b-g1-q4_k_m.gguf",
            MirrorUrl: "https://hf-mirror.com/zhiyuan8/RWKV-v7-2.9B-G1-GGUF/resolve/main/rwkv7-2.9b-g1-q4_k_m.gguf",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 8192,
            DiskSizeMB: 1880,
            Description: "高性能本地模型，C-Eval 49.6%，CMMLU 52.3%。推理能力更强，适合复杂任务。",
            Layer: ModelLayer.L1,
            EngineType: "gguf"),

        new LocalModelInfo(
            Version: "qwen2.5-1.5b-q4",
            Name: "Qwen2.5 1.5B (Q4_K_M) - 中文最强",
            Url: "https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf",
            MirrorUrl: "https://hf-mirror.com/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 4096,
            DiskSizeMB: 1100,
            Description: "通义千问开源版，中文能力显著优于 RWKV。适合中文重度用户。",
            Layer: ModelLayer.L1,
            EngineType: "gguf"),

        new LocalModelInfo(
            Version: "qwen2.5-3b-q4",
            Name: "Qwen2.5 3B (Q4_K_M) - 中文旗舰",
            Url: "https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf",
            MirrorUrl: "https://hf-mirror.com/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 8192,
            DiskSizeMB: 2000,
            Description: "Qwen2.5 中文旗舰小模型，同尺寸中文最强。适合专业中文场景。",
            Layer: ModelLayer.L1,
            EngineType: "gguf"),

        // ==================== L2: Deep Models (GGUF) ====================
        new LocalModelInfo(
            Version: "qwen2.5-7b-q4",
            Name: "Qwen2.5 7B (Q4_K_M) - 深度推理",
            Url: "https://huggingface.co/Qwen/Qwen2.5-7B-Instruct-GGUF/resolve/main/qwen2.5-7b-instruct-q4_k_m.gguf",
            MirrorUrl: "https://hf-mirror.com/Qwen/Qwen2.5-7B-Instruct-GGUF/resolve/main/qwen2.5-7b-instruct-q4_k_m.gguf",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 16384,
            DiskSizeMB: 4400,
            Description: "Qwen2.5 7B 深度模型，数学/代码/推理能力显著优于小模型。适合复杂分析任务。",
            Layer: ModelLayer.L2,
            EngineType: "gguf"),

        new LocalModelInfo(
            Version: "qwen2.5-14b-q4",
            Name: "Qwen2.5 14B (Q4_K_M) - 旗舰深度",
            Url: "https://huggingface.co/Qwen/Qwen2.5-14B-Instruct-GGUF/resolve/main/qwen2.5-14b-instruct-q4_k_m.gguf",
            MirrorUrl: "https://hf-mirror.com/Qwen/Qwen2.5-14B-Instruct-GGUF/resolve/main/qwen2.5-14b-instruct-q4_k_m.gguf",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 32768,
            DiskSizeMB: 9000,
            Description: "Qwen2.5 14B 旗舰，接近 GPT-3.5 水平。适合专业级深度推理、代码生成。",
            Layer: ModelLayer.L2,
            EngineType: "gguf"),

        new LocalModelInfo(
            Version: "deepseek-r1-distill-qwen-7b-q4",
            Name: "DeepSeek-R1-Distill-Qwen-7B (Q4_K_M) - 推理增强",
            Url: "https://huggingface.co/unsloth/DeepSeek-R1-Distill-Qwen-7B-GGUF/resolve/main/deepseek-r1-distill-qwen-7b-q4_k_m.gguf",
            MirrorUrl: "https://hf-mirror.com/unsloth/DeepSeek-R1-Distill-Qwen-7B-GGUF/resolve/main/deepseek-r1-distill-qwen-7b-q4_k_m.gguf",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 16384,
            DiskSizeMB: 4700,
            Description: "DeepSeek R1 蒸馏版，推理能力增强。适合数学证明、逻辑推理、代码调试。",
            Layer: ModelLayer.L2,
            EngineType: "gguf"),

        new LocalModelInfo(
            Version: "deepseek-r1-distill-llama-8b-q4",
            Name: "DeepSeek-R1-Distill-Llama-8B (Q4_K_M) - 推理增强",
            Url: "https://huggingface.co/unsloth/DeepSeek-R1-Distill-Llama-8B-GGUF/resolve/main/deepseek-r1-distill-llama-8b-q4_k_m.gguf",
            MirrorUrl: "https://hf-mirror.com/unsloth/DeepSeek-R1-Distill-Llama-8B-GGUF/resolve/main/deepseek-r1-distill-llama-8b-q4_k_m.gguf",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 16384,
            DiskSizeMB: 4900,
            Description: "DeepSeek R1 + Llama 8B 蒸馏版，英文推理能力强。适合英文技术场景。",
            Layer: ModelLayer.L2,
            EngineType: "gguf"),
    };

    public static LocalModelInfo SelectBestModel(long availableMemoryMB, ModelLayer layer, string preferredLanguage = "zh")
    {
        var layerModels = AvailableModels.Where(m => m.Layer == layer).ToList();
        if (layerModels.Count == 0) return AvailableModels.First();

        if (layer == ModelLayer.L0)
        {
            if (availableMemoryMB >= 4096)
                return layerModels.First(m => m.Version == "bge-m3-onnx");
            if (availableMemoryMB >= 2048)
                return layerModels.First(m => m.Version == "bge-large-zh-v1.5-onnx");
            return layerModels.First(m => m.Version == "bge-small-zh-v1.5-onnx");
        }

        if (layer == ModelLayer.L1)
        {
            if (preferredLanguage.Equals("zh", StringComparison.OrdinalIgnoreCase))
            {
                if (availableMemoryMB >= 8192)
                    return layerModels.First(m => m.Version == "qwen2.5-3b-q4");
                if (availableMemoryMB >= 4096)
                    return layerModels.First(m => m.Version == "qwen2.5-1.5b-q4");
            }
            if (availableMemoryMB >= 8192)
                return layerModels.First(m => m.Version == "rwkv7-g1-2.9b-q4");
            if (availableMemoryMB >= 4096)
                return layerModels.First(m => m.Version == "rwkv7-g1-1.5b-q4");
            return layerModels.First(m => m.Version == "rwkv7-g1-0.4b-q4");
        }

        // L2
        if (availableMemoryMB >= 32768)
            return layerModels.First(m => m.Version == "qwen2.5-14b-q4");
        if (availableMemoryMB >= 16384)
        {
            if (preferredLanguage.Equals("zh", StringComparison.OrdinalIgnoreCase))
                return layerModels.First(m => m.Version == "qwen2.5-7b-q4");
            return layerModels.First(m => m.Version == "deepseek-r1-distill-llama-8b-q4");
        }
        return layerModels.First(m => m.Version == "qwen2.5-7b-q4");
    }

    public static LocalModelInfo? GetByVersion(string version)
    {
        return AvailableModels.FirstOrDefault(m => m.Version.Equals(version, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<LocalModelInfo> GetByLayer(ModelLayer layer)
    {
        return AvailableModels.Where(m => m.Layer == layer).ToList();
    }

    public static IReadOnlyList<LocalModelInfo> GetByEngineType(string engineType)
    {
        return AvailableModels.Where(m => m.EngineType.Equals(engineType, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static IReadOnlyList<LocalModelInfo> GetByLayerAndEngine(ModelLayer layer, string engineType)
    {
        return AvailableModels.Where(m => m.Layer == layer && m.EngineType.Equals(engineType, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static long DetectAvailableMemoryMB()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo("powershell", "-NoProfile -Command \"[math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1MB)\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                process.Start();
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                if (long.TryParse(output, out var mb) && mb > 0)
                    return mb;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var meminfo = File.ReadAllText("/proc/meminfo");
                var lines = meminfo.Split('\n');
                foreach (var line in lines)
                {
                    if (line.StartsWith("MemTotal:"))
                    {
                        var parts = line.Split(new[] { ' ', ':' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
                            return kb / 1024;
                    }
                }
            }
        }
        catch
        {
        }

        return 16384;
    }
}
