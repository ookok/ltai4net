using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace LTAI.Core.Governors;

public record RwkvModelInfo(
    string Version,
    string Name,
    string Url,
    string Sha256,
    long RecommendedMemoryMB,
    long DiskSizeMB,
    string Description,
    string EngineType = "gguf");

public static class RwkvModelRegistry
{
    public static readonly IReadOnlyList<RwkvModelInfo> AvailableModels = new[]
    {
        // RWKV-7 G1 系列 (推荐)
        new RwkvModelInfo(
            Version: "rwkv7-g1-1.5b-q4",
            Name: "RWKV-7 G1 1.5B (Q4_K_M) - 中文优化",
            Url: "https://huggingface.co/zhiyuan8/RWKV-v7-1.5B-G1-GGUF/resolve/main/rwkv7-1.5b-g1-q4_k_m.gguf",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 4096,
            DiskSizeMB: 990,
            Description: "轻量级本地模型，World 词表支持 100+ 语言。C-Eval 42.7%，适合日常对话。",
            EngineType: "gguf"),

        new RwkvModelInfo(
            Version: "rwkv7-g1-2.9b-q4",
            Name: "RWKV-7 G1 2.9B (Q4_K_M) - 中文增强",
            Url: "https://huggingface.co/zhiyuan8/RWKV-v7-2.9B-G1-GGUF/resolve/main/rwkv7-2.9b-g1-q4_k_m.gguf",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 8192,
            DiskSizeMB: 1880,
            Description: "高性能本地模型，C-Eval 49.6%，CMMLU 52.3%。推理能力更强，适合复杂任务。",
            EngineType: "gguf"),

        new RwkvModelInfo(
            Version: "rwkv7-g1-0.4b-q4",
            Name: "RWKV-7 G1 0.4B (Q4_K_M) - 极轻量",
            Url: "https://huggingface.co/Mungert/rwkv7-0.4B-g1-GGUF/resolve/main/rwkv7-0.4b-g1-q4_k_m.gguf",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 2048,
            DiskSizeMB: 250,
            Description: "极轻量模型，适合 2GB+ 内存设备或嵌入式场景。仅基础对话。",
            EngineType: "gguf"),

        // Qwen2.5 GGUF 系列 (中文能力更强)
        new RwkvModelInfo(
            Version: "qwen2.5-1.5b-q4",
            Name: "Qwen2.5 1.5B (Q4_K_M) - 中文最强",
            Url: "https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 4096,
            DiskSizeMB: 1100,
            Description: "通义千问开源版，中文能力显著优于 RWKV。适合中文重度用户。",
            EngineType: "gguf"),

        new RwkvModelInfo(
            Version: "qwen2.5-3b-q4",
            Name: "Qwen2.5 3B (Q4_K_M) - 中文旗舰",
            Url: "https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 8192,
            DiskSizeMB: 2000,
            Description: "Qwen2.5 中文旗舰小模型，同尺寸中文最强。适合专业中文场景。",
            EngineType: "gguf"),

        // ONNX Transformer 系列
        new RwkvModelInfo(
            Version: "phi3-mini-onnx",
            Name: "Phi-3 Mini (ONNX) - 微软官方",
            Url: "https://huggingface.co/microsoft/Phi-3-mini-4k-instruct-onnx/resolve/main/model.onnx",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 4096,
            DiskSizeMB: 2400,
            Description: "微软官方 ONNX 模型，兼容性好。适合需要 ONNX 生态的场景。",
            EngineType: "onnx"),

        new RwkvModelInfo(
            Version: "gemma-2b-onnx",
            Name: "Gemma 2B (ONNX) - Google",
            Url: "https://huggingface.co/google/gemma-2b-it-onnx/resolve/main/model.onnx",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 4096,
            DiskSizeMB: 2600,
            Description: "Google Gemma 开源版，英文能力强。适合英文为主的用户。",
            EngineType: "onnx")
    };

    public static RwkvModelInfo SelectBestModel(long availableMemoryMB, string preferredLanguage = "zh")
    {
        // 中文用户优先推荐 Qwen，英文用户推荐 RWKV
        if (preferredLanguage.Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            if (availableMemoryMB >= 8192)
                return AvailableModels.First(m => m.Version == "qwen2.5-3b-q4");
            if (availableMemoryMB >= 4096)
                return AvailableModels.First(m => m.Version == "qwen2.5-1.5b-q4");
        }

        if (availableMemoryMB >= 8192)
            return AvailableModels.First(m => m.Version == "rwkv7-g1-2.9b-q4");
        if (availableMemoryMB >= 4096)
            return AvailableModels.First(m => m.Version == "rwkv7-g1-1.5b-q4");
        return AvailableModels.First(m => m.Version == "rwkv7-g1-0.4b-q4");
    }

    public static RwkvModelInfo? GetByVersion(string version)
    {
        return AvailableModels.FirstOrDefault(m => m.Version.Equals(version, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<RwkvModelInfo> GetByEngineType(string engineType)
    {
        return AvailableModels.Where(m => m.EngineType.Equals(engineType, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static long DetectAvailableMemoryMB()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo("powershell", "-Command \"(Get-CimInstance Win32_PhysicalMemory | Measure-Object Capacity -Sum).Sum / 1MB\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(2000);
                if (long.TryParse(output.Trim(), out var bytes))
                    return bytes / 1024 / 1024;
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

        return 4096;
    }
}
