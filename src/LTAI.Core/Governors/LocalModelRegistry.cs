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
            Url: "https://huggingface.co/Xenova/bge-small-zh-v1.5/resolve/main/onnx/model.onnx",
            MirrorUrl: "https://hf-mirror.com/Xenova/bge-small-zh-v1.5/resolve/main/onnx/model.onnx",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 1024,
            DiskSizeMB: 95,
            Description: "BGE 轻量中文版，93MB，适合内存受限场景。",
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

        // Jina Embeddings v5 Omni
        new LocalModelInfo(
            Version: "jina-embeddings-v5-omni-small",
            Name: "Jina-v5-Omni-Small (ONNX) - 多模态嵌入",
            Url: "https://huggingface.co/jinaai/jina-embeddings-v5-omni/resolve/main/onnx_small/model.onnx",
            MirrorUrl: "https://hf-mirror.com/jinaai/jina-embeddings-v5-omni/resolve/main/onnx_small/model.onnx",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 2048,
            DiskSizeMB: 500,
            Description: "Jina AI v5 全模态嵌入模型 (768-dim)，支持文本+图像+音频。8K上下文，任务自适应。",
            Layer: ModelLayer.L0,
            EngineType: "onnx"),

        new LocalModelInfo(
            Version: "jina-embeddings-v5-omni-nano",
            Name: "Jina-v5-Omni-Nano (ONNX) - 轻量多模态嵌入",
            Url: "https://huggingface.co/jinaai/jina-embeddings-v5-omni/resolve/main/onnx_nano/model.onnx",
            MirrorUrl: "https://hf-mirror.com/jinaai/jina-embeddings-v5-omni/resolve/main/onnx_nano/model.onnx",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 1024,
            DiskSizeMB: 200,
            Description: "Jina AI v5 Nano 嵌入模型 (512-dim)，轻量全模态。4GB边缘设备即可运行。",
            Layer: ModelLayer.L0,
            EngineType: "onnx"),

        // ==================== L0: OCR Models (ONNX, lightweight) ====================
        new LocalModelInfo(
            Version: "rapidocr-det-v4-onnx",
            Name: "RapidOCR Detection (PP-OCRv4 DB) — 文本检测",
            Url: "https://huggingface.co/SWHL/RapidOCR/resolve/main/ch_PP-OCRv4_det_infer.onnx",
            MirrorUrl: "https://hf-mirror.com/SWHL/RapidOCR/resolve/main/ch_PP-OCRv4_det_infer.onnx",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 512,
            DiskSizeMB: 5,
            Description: "PP-OCRv4 DBNet 文本检测模型。识别图片中文字区域位置。",
            Layer: ModelLayer.L0,
            EngineType: "onnx"),

        new LocalModelInfo(
            Version: "rapidocr-rec-v4-onnx",
            Name: "RapidOCR Recognition (PP-OCRv4 CRNN) — 文字识别",
            Url: "https://huggingface.co/SWHL/RapidOCR/resolve/main/ch_PP-OCRv4_rec_infer.onnx",
            MirrorUrl: "https://hf-mirror.com/SWHL/RapidOCR/resolve/main/ch_PP-OCRv4_rec_infer.onnx",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 512,
            DiskSizeMB: 9,
            Description: "PP-OCRv4 CRNN 文字识别模型。将检测到的文字区域识别为中文/英文文本。6623字符集。",
            Layer: ModelLayer.L0,
            EngineType: "onnx"),

        new LocalModelInfo(
            Version: "rapidocr-vocab",
            Name: "RapidOCR Vocabulary (PP-OCRv4 keys) — 字符映射表",
            Url: "https://huggingface.co/SWHL/RapidOCR/resolve/main/ppocr_keys_v1.txt",
            MirrorUrl: "https://hf-mirror.com/SWHL/RapidOCR/resolve/main/ppocr_keys_v1.txt",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 256,
            DiskSizeMB: 1,
            Description: "PP-OCRv4 字符映射表。6623个中英文常用字符。",
            Layer: ModelLayer.L0,
            EngineType: "text"),

        // ==================== L0: TTS Models (ONNX) ====================
        new LocalModelInfo(
            Version: "supertonic-3-onnx",
            Name: "Supertonic 3 (ONNX) - 多语言 TTS",
            Url: "https://huggingface.co/Supertone/supertonic-3",
            MirrorUrl: "https://hf-mirror.com/Supertone/supertonic-3",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 2048,
            DiskSizeMB: 400,
            Description: "Supertonic 3 闪电 TTS 模型，31语言支持。99M参数，44.1kHz输出，支持表达标签。需 Git LFS 克隆完整 assets 目录。",
            Layer: ModelLayer.L0,
            EngineType: "onnx"),

        // ==================== L0: Tool-Calling Models (ONNX, Distilled) ====================
        new LocalModelInfo(
            Version: "needle-26m-onnx",
            Name: "Needle 26M (ONNX) - 端侧工具路由",
            Url: "https://huggingface.co/CactusCompute/needle-26m",
            MirrorUrl: "https://hf-mirror.com/CactusCompute/needle-26m",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 128,
            DiskSizeMB: 52,
            Description: "Needle 26M 蒸馏工具调用模型。移除FFN层，专为端侧意图识别+工具路由。26M参数，512MB设备即可运行。",
            Layer: ModelLayer.L0,
            EngineType: "onnx"),

        // ==================== L1: Small ONNX LLMs (Edge-Native) ====================
        new LocalModelInfo(
            Version: "smollm2-360m-onnx",
            Name: "SmolLM2 360M (ONNX) - 极致边缘",
            Url: "https://huggingface.co/HuggingFaceTB/SmolLM2-360M-Instruct/resolve/main/onnx/model.onnx",
            MirrorUrl: "https://hf-mirror.com/HuggingFaceTB/SmolLM2-360M-Instruct/resolve/main/onnx/model.onnx",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 1024,
            DiskSizeMB: 280,
            Description: "HuggingFace 极轻量模型，2GB设备可运行。基础对话+意图识别，L1 ONNX链路入口。",
            Layer: ModelLayer.L1,
            EngineType: "onnx"),

        new LocalModelInfo(
            Version: "smollm2-1.7b-onnx",
            Name: "SmolLM2 1.7B (ONNX) - 轻量推理",
            Url: "https://huggingface.co/HuggingFaceTB/SmolLM2-1.7B-Instruct/resolve/main/onnx/model.onnx",
            MirrorUrl: "https://hf-mirror.com/HuggingFaceTB/SmolLM2-1.7B-Instruct/resolve/main/onnx/model.onnx",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 4096,
            DiskSizeMB: 1300,
            Description: "SmolLM2 1.7B 轻量版。英文为主，推理能力接近 Llama 3B。ONNX原生链路可直接调用。",
            Layer: ModelLayer.L1,
            EngineType: "onnx"),

        new LocalModelInfo(
            Version: "qwen2.5-0.5b-onnx",
            Name: "Qwen2.5 0.5B (ONNX) - 中文边缘推理",
            Url: "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct/resolve/main/onnx/model.onnx",
            MirrorUrl: "https://hf-mirror.com/Qwen/Qwen2.5-0.5B-Instruct/resolve/main/onnx/model.onnx",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 2048,
            DiskSizeMB: 400,
            Description: "通义千问 0.5B 边缘版，中文原生。4GB设备流畅运行。L1链路中文入口。",
            Layer: ModelLayer.L1,
            EngineType: "onnx"),

        new LocalModelInfo(
            Version: "qwen2.5-1.5b-onnx",
            Name: "Qwen2.5 1.5B (ONNX) - 中文主力",
            Url: "https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct/resolve/main/onnx/model.onnx",
            MirrorUrl: "https://hf-mirror.com/Qwen/Qwen2.5-1.5B-Instruct/resolve/main/onnx/model.onnx",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 4096,
            DiskSizeMB: 1200,
            Description: "Qwen2.5 1.5B 中文主力，同尺寸中文最强。8GB设备可跑OnnxParallelEngine多模型并发。",
            Layer: ModelLayer.L1,
            EngineType: "onnx"),

        new LocalModelInfo(
            Version: "phi3.5-mini-onnx",
            Name: "Phi-3.5-Mini (ONNX) - 英文推理增强",
            Url: "https://huggingface.co/microsoft/Phi-3.5-mini-onnx/resolve/main/model.onnx",
            MirrorUrl: "https://hf-mirror.com/microsoft/Phi-3.5-mini-onnx/resolve/main/model.onnx",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 8192,
            DiskSizeMB: 2500,
            Description: "微软 Phi-3.5 Mini ONNX，1.3B MoE。推理+工具调用+代码能力均衡。推荐8GB+设备。",
            Layer: ModelLayer.L1,
            EngineType: "onnx"),

        // ==================== L1: Fast Models (GGUF — legacy compat) ====================
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

        // ==================== L0: Supertonic TTS Models (ONNX) ====================
        new LocalModelInfo(
            Version: "supertonic-3-onnx",
            Name: "Supertonic 3 (ONNX) - 31语言TTS",
            Url: "https://huggingface.co/Supertone/supertonic-3/resolve/main/model.onnx",
            MirrorUrl: "https://hf-mirror.com/Supertone/supertonic-3/resolve/main/model.onnx",
            Sha256: "auto_verify",
            RecommendedMemoryMB: 512,
            DiskSizeMB: 400,
            Description: "Supertone Supertonic 3，31语言端侧TTS。99M参数，CPU推理，44.1kHz输出。支持表情标签。",
            Layer: ModelLayer.L0,
            EngineType: "onnx"),
    };

    public static LocalModelInfo SelectBestModel(long availableMemoryMB, ModelLayer layer, string preferredLanguage = "zh", string preferredEngine = "onnx")
    {
        var layerModels = AvailableModels.Where(m => m.Layer == layer).ToList();
        if (layerModels.Count == 0) return AvailableModels.First();

        // Prefer the specified engine type, fallback to any
        var byEngine = layerModels.Where(m => m.EngineType.Equals(preferredEngine, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byEngine.Count > 0) layerModels = byEngine;

        if (layer == ModelLayer.L0)
        {
            // Jina preferred for ONNX-native pipeline
            if (availableMemoryMB >= 2048 && preferredEngine == "onnx")
                return layerModels.FirstOrDefault(m => m.Version.Contains("jina-small", StringComparison.OrdinalIgnoreCase)) ?? layerModels[0];
            if (availableMemoryMB >= 4096)
                return layerModels.FirstOrDefault(m => m.Version.Contains("m3", StringComparison.OrdinalIgnoreCase)) ?? layerModels[0];
            if (availableMemoryMB >= 2048)
                return layerModels.FirstOrDefault(m => m.Version.Contains("large", StringComparison.OrdinalIgnoreCase)) ?? layerModels[0];
            return layerModels.FirstOrDefault(m => m.Version.Contains("nano", StringComparison.OrdinalIgnoreCase) || m.Version.Contains("small", StringComparison.OrdinalIgnoreCase)) ?? layerModels[0];
        }

        if (layer == ModelLayer.L1)
        {
            // ONNX preferred: runs natively on edge, supports OnnxParallelEngine + OnnxModelPipeline
            if (preferredLanguage.Equals("zh", StringComparison.OrdinalIgnoreCase))
            {
                if (availableMemoryMB >= 4096)
                    return layerModels.FirstOrDefault(m => m.Version.Contains("qwen2.5-1.5b-onnx", StringComparison.OrdinalIgnoreCase))
                        ?? layerModels.FirstOrDefault(m => m.Version.Contains("qwen2.5-1.5b", StringComparison.OrdinalIgnoreCase))
                        ?? layerModels[0];
                if (availableMemoryMB >= 2048)
                    return layerModels.FirstOrDefault(m => m.Version.Contains("qwen2.5-0.5b-onnx", StringComparison.OrdinalIgnoreCase))
                        ?? layerModels.FirstOrDefault(m => m.Version.Contains("qwen2.5-0.5b", StringComparison.OrdinalIgnoreCase))
                        ?? layerModels[0];
            }
            if (availableMemoryMB >= 8192)
                return layerModels.FirstOrDefault(m => m.Version.Contains("phi3", StringComparison.OrdinalIgnoreCase)) ?? layerModels[0];
            if (availableMemoryMB >= 4096)
                return layerModels.FirstOrDefault(m => m.Version.Contains("1.7b", StringComparison.OrdinalIgnoreCase)) ?? layerModels[0];
            return layerModels.FirstOrDefault(m => m.Version.Contains("360m", StringComparison.OrdinalIgnoreCase)) ?? layerModels[0];
        }

        // L2
        if (availableMemoryMB >= 32768)
            return layerModels.FirstOrDefault(m => m.Version == "qwen2.5-14b-q4") ?? layerModels[0];
        if (availableMemoryMB >= 16384)
        {
            if (preferredLanguage.Equals("zh", StringComparison.OrdinalIgnoreCase))
                return layerModels.FirstOrDefault(m => m.Version == "qwen2.5-7b-q4") ?? layerModels[0];
            return layerModels.FirstOrDefault(m => m.Version == "deepseek-r1-distill-llama-8b-q4") ?? layerModels[0];
        }
        return layerModels.FirstOrDefault(m => m.Version == "qwen2.5-7b-q4") ?? layerModels[0];
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
