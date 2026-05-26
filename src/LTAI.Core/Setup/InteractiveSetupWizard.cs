using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LTAI.Core.Configuration;
using LTAI.Core.Governors;

namespace LTAI.Core.Setup;

public class InteractiveSetupWizard
{
    private readonly string _configPath;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ModelDownloader _modelDownloader;
    private LTAIOptions _options;
    private bool _isDirty;

    private static readonly HttpClient _hfClient = new() { Timeout = TimeSpan.FromMinutes(30) };

    public InteractiveSetupWizard(string configPath, IHttpClientFactory? httpClientFactory = null)
    {
        _configPath = configPath;
        _httpClientFactory = httpClientFactory;
        _options = LoadOrCreateConfig();
        _isDirty = false;
        _modelDownloader = new ModelDownloader(httpClientFactory?.CreateClient());
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine();
        Console.WriteLine("┌─────────────────────────────────────────────────────┐");
        Console.WriteLine("│           LTAI 配置向导 (可重复运行)                  │");
        Console.WriteLine("└─────────────────────────────────────────────────────┘");
        Console.WriteLine();

        var hwInfo = DetectHardwareCapabilities();
        Console.WriteLine($"硬件检测: {hwInfo.CpuCores} 核 | {hwInfo.MemoryMB}MB 内存 | GPU: {(hwInfo.HasGpu ? hwInfo.GpuName : "无")} | NPU: {(hwInfo.HasNpu ? "有" : "无")}");
        Console.WriteLine($"推荐引擎: {hwInfo.RecommendedEngine.ToUpper()}");
        Console.WriteLine();
        Console.WriteLine("系统已预配置默认值，仅需提供 API Key 即可使用。");
        Console.WriteLine("直接按 Enter 跳过，将使用本地模式运行。");
        Console.WriteLine();

        await ConfigureLayerAsync("L0", "Embedding 层 (向量检索 / 知识库)", hwInfo, cancellationToken);
        await ConfigureLayerAsync("L1", "Fast 层 (快速推理/日常对话)", hwInfo, cancellationToken);
        await ConfigureLayerAsync("L2", "Deep 层 (深度推理/复杂任务)", hwInfo, cancellationToken);

        if (_isDirty)
        {
            SaveConfig();
            Console.WriteLine();
            Console.WriteLine("✓ 配置已保存");
        }

        Console.WriteLine();
        Console.WriteLine("启动中...");
        Console.WriteLine();
    }

    private LTAIOptions LoadOrCreateConfig()
    {
        if (File.Exists(_configPath))
        {
            try
            {
                var json = File.ReadAllText(_configPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("LTAI", out var ltai))
                    return ltai.Deserialize<LTAIOptions>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
                return doc.RootElement.Deserialize<LTAIOptions>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            }
            catch { /* intentional: cleanup may fail */ }
                return new LTAIOptions();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_configPath) ?? throw new InvalidOperationException("Config path has no parent directory"));
        return new LTAIOptions();
    }

    private void SaveConfig()
    {
        var wrapper = new { LTAI = _options };
        var json = JsonSerializer.Serialize(wrapper, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_configPath, json);
    }

    private async Task ConfigureLayerAsync(string layerName, string description, HardwareInfo hwInfo, CancellationToken ct)
    {
        Console.WriteLine($"━━━ {layerName}: {description} ━━━");
        Console.WriteLine();

        if (layerName == "L0")
        {
            Console.WriteLine("选择 L0 嵌入层模式：");
            Console.WriteLine();
            Console.WriteLine("  [1] 云端 API 模式");
            Console.WriteLine("      使用在线 Embedding API，需要 API Key");
            Console.WriteLine("  [2] 本地模式 (离线运行，隐私安全)");
            Console.WriteLine("      纯本地 ONNX 推理，零网络依赖，完全隐私");
            Console.WriteLine("      支持: BGE / Jina v5 Omni (多模态) / Supertonic TTS");
            Console.WriteLine("  [Enter] = 2 (本地模式)");
            Console.WriteLine();

            var modeChoice = ReadChoice("选择模式", "2", new[] { "1", "2" });
            if (modeChoice == "1")
            {
                await ConfigureApiProviderSelectionAsync(layerName, ct).ConfigureAwait(false);
                Console.WriteLine();
                return;
            }

            await OfferL0LocalModelAsync(hwInfo, ct).ConfigureAwait(false);
            return;
        }

        await ConfigureApiProviderSelectionAsync(layerName, ct).ConfigureAwait(false);
        Console.WriteLine();
    }

    private async Task ConfigureApiProviderSelectionAsync(string layerName, CancellationToken ct)
    {
        var registry = new ProviderRegistry();
        var allProviders = registry.AllProviders.ToList();

        var cloud = new List<(string Name, string BaseUrl, List<string> Caps, bool Suitable)>();
        foreach (var p in allProviders)
        {
            var url = registry.GetBaseUrl(p);
            if (url == null) continue;
            if (url.Contains("localhost") || url.Contains("127.0.0.1") || url.Contains("0.0.0.0") || url.Contains(".local")) continue;

            var caps = registry.GetCapabilities(p);
            // L0: only show providers with embedding capability
            if (layerName == "L0" && !caps.Contains("embedding"))
                continue;

            cloud.Add((p, url, caps, true));
        }

        if (cloud.Count == 0)
        {
            if (layerName == "L0")
                Console.WriteLine("  ⚠️  无可用云端嵌入提供商，请使用本地 ONNX 模型");
            else
                Console.WriteLine("  ⚠️  无可用云端提供商");
            return;
        }

        var title = layerName == "L0"
            ? "选择嵌入向量云端提供商 (仅显示支持 embedding 的提供商)："
            : "选择云端提供商：";
        Console.WriteLine(title);
        Console.WriteLine();

        for (int i = 0; i < cloud.Count; i++)
        {
            var c = cloud[i];
            var capStr = string.Join(",", c.Caps);
            Console.WriteLine($"  [{i + 1,2}] {c.Name,-14} {c.BaseUrl,-50} [{capStr}]");
        }
        Console.WriteLine("  [Enter] 跳过");
        Console.WriteLine();

        var choice = ReadLine("选择编号或回车跳过");
        if (string.IsNullOrWhiteSpace(choice))
        {
            Console.WriteLine($"  ⏭️  跳过 {layerName} 配置");
            return;
        }

        string selectedProvider;
        if (!int.TryParse(choice, out var idx) || idx < 1 || idx > cloud.Count)
        {
            Console.WriteLine("  ⚠️  无效选择");
            return;
        }
        selectedProvider = cloud[idx - 1].Name;

        var endpoint = registry.GetBaseUrl(selectedProvider) ?? throw new InvalidOperationException($"No base URL found for provider {selectedProvider}");
        Console.WriteLine();
        Console.WriteLine($"提供商: {selectedProvider}");
        Console.WriteLine($"端点: {endpoint}");
        Console.WriteLine();

        var apiKey = ReadSecret($"API Key ({selectedProvider})");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("  ⚠️  未提供 API Key");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("  正在获取模型列表...");

        var availableModels = await FetchModelListAsync(endpoint, apiKey, ct).ConfigureAwait(false);

        string recommended;
        if (availableModels.Count > 0)
        {
            var suitable = FilterModelsForLayer(layerName, availableModels);

            if (suitable.Count == 0)
            {
                Console.WriteLine($"  ⚠️  该提供商无可用的 {layerName} 层模型");
                Console.WriteLine("  所有可用模型：");
                foreach (var m in availableModels.Take(20))
                    Console.WriteLine($"    - {m}");
                recommended = ReadLine("• 手动输入模型名称，或回车跳过") ?? "";
                if (string.IsNullOrWhiteSpace(recommended))
                {
                    Console.WriteLine("  ⚠️  未提供模型名称");
                    return;
                }
            }
            else
            {
                recommended = suitable[0];
                Console.WriteLine();
                Console.WriteLine($"  {layerName} 层可用模型 ({suitable.Count} 个)：");
                Console.WriteLine($"  推荐: {recommended}");
                Console.WriteLine();

                if (suitable.Count > 1)
                {
                    Console.WriteLine("  全部可选：");
                    for (int i = 0; i < suitable.Count; i++)
                        Console.WriteLine($"    [{i + 1}] {suitable[i]}");
                    Console.WriteLine("    [Enter] 使用推荐");
                    Console.WriteLine();

                    var modelChoice = ReadLine("选择模型编号");
                    if (!string.IsNullOrWhiteSpace(modelChoice) && int.TryParse(modelChoice, out var mi) && mi >= 1 && mi <= suitable.Count)
                        recommended = suitable[mi - 1];
                }
            }
        }
        else
        {
            Console.WriteLine("  ⚠️  无法获取模型列表 (API 可能不支持 /models 端点)");
            Console.WriteLine();

            var defaultModel = layerName switch
            {
                "L0" => "text-embedding-v1",
                "L1" => "deepseek-chat",
                "L2" => "deepseek-chat",
                _ => ""
            };

            recommended = ReadLine($"模型名称 (默认: {defaultModel})") ?? "";
            if (string.IsNullOrWhiteSpace(recommended))
                recommended = defaultModel;
        }

        _options.AI.Providers[selectedProvider] = new ProviderConfig
        {
            Endpoint = endpoint,
            ApiKey = "",
            Model = recommended
        };

        var envVarName = GetProviderEnvVar(selectedProvider, layerName);
        SetPersistentEnvVar(envVarName, apiKey);

        var layerConfig = _options.AI.GetLayerConfig(layerName);
        layerConfig.GetType().GetProperty("Provider")?.SetValue(layerConfig, selectedProvider);
        layerConfig.GetType().GetProperty("Model")?.SetValue(layerConfig, recommended);
        _isDirty = true;

        Console.WriteLine();
        Console.WriteLine($"  ✓ {layerName} → {selectedProvider} / {recommended}");
        Console.WriteLine($"  🔑 API Key 已存入环境变量 {envVarName}（永久保存）");
    }

    private static async Task<List<string>> FetchModelListAsync(string endpoint, string apiKey, CancellationToken ct)
    {
        try
        {
            var baseUrl = endpoint.TrimEnd('/');
            var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
            request.Headers.Authorization = new global::System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            var response = await _hfClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseBody);
            var models = new List<string>();

            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id))
                        models.Add(id.GetString() ?? string.Empty);
                }
            }

            return models;
        }
        catch { /* intentional: cleanup may fail */ }
            return new List<string>();
    }

    private static List<string> FilterModelsForLayer(string layerName, List<string> allModels)
    {
        const StringComparison c = StringComparison.OrdinalIgnoreCase;

        if (layerName == "L0")
        {
            // Embedding-capable models
            return allModels
                .Where(m =>
                    m.Contains("embed", c) || m.Contains("Embed", c) ||
                    m.Contains("bge-", c) || m.Contains("BGE", c) ||
                    m.Contains("text-embedding", c))
                .OrderByDescending(m => m.Contains("large", c) || m.Contains("v4", c) || m.Contains("v3", c) ? 2 :
                                       m.Contains("small", c) || m.Contains("v1", c) ? 0 : 1)
                .ThenBy(m => m.Length)
                .ToList();
        }

        if (layerName == "L1")
        {
            // Fast/cheap chat models
            return allModels
                .Where(m =>
                    !m.Contains("embed", c) && !m.Contains("Embed", c) &&
                    !m.Contains("rerank", c) && !m.Contains("image", c) &&
                    !m.Contains("vl-", c) && !m.Contains("vision", c) &&
                    !m.Contains("ocr", c) && !m.Contains("asr", c) &&
                    !m.Contains("tts", c) && !m.Contains("speech", c) &&
                    !m.Contains("omni", c))
                .OrderBy(m => m.Contains("flash", c) || m.Contains("turbo", c) || m.Contains("lite", c) ? 3 :
                              m.Contains("plus", c) || m.Contains("air", c) ? 2 :
                              m.Contains("pro", c) || m.Contains("max", c) || m.Contains("ultra", c) ? 0 : 1)
                .ThenBy(m => m.Length)
                .ToList();
        }

        // L2: Deep/reasoning models
        return allModels
            .Where(m =>
                !m.Contains("embed", c) && !m.Contains("Embed", c) &&
                !m.Contains("rerank", c) && !m.Contains("image", c) &&
                !m.Contains("vl-", c) && !m.Contains("vision", c) &&
                !m.Contains("ocr", c) && !m.Contains("asr", c) &&
                !m.Contains("tts", c) && !m.Contains("speech", c) &&
                !m.Contains("omni", c) && !m.Contains("flash", c) &&
                !m.Contains("turbo", c) && !m.Contains("lite", c))
            .OrderByDescending(m => m.Contains("pro", c) || m.Contains("max", c) || m.Contains("ultra", c) ? 3 :
                                    m.Contains("reasoning", c) || m.Contains("think", c) || m.Contains("r1", c) ? 2 : 1)
            .ThenByDescending(m => m.Length)
            .ToList();
    }

    /// <summary>
    /// L0 嵌入层：优先推荐本地 ONNX 模型下载。
    /// 返回 true 表示用户已选择本地模型并完成配置。
    /// </summary>
    private async Task<bool> OfferL0LocalModelAsync(HardwareInfo hwInfo, CancellationToken ct)
    {
        Console.WriteLine("📦 本地 ONNX 模型 (离线推理，零网络依赖)");
        Console.WriteLine();

        var layer = ModelLayer.L0;
        var models = LocalModelRegistry.GetByLayer(layer)
            .Where(m => m.EngineType == "onnx" && !m.Version.Contains("ocr", StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.DiskSizeMB)
            .ToList();

        if (models.Count == 0)
        {
            Console.WriteLine("  ⚠️  无可用本地模型");
            return false;
        }

        var recommended = LocalModelRegistry.SelectBestModel(hwInfo.MemoryMB, layer);
        if (recommended.EngineType != "onnx" || recommended.Version.Contains("ocr"))
            recommended = models.FirstOrDefault(m => m.Version.Contains("small", StringComparison.OrdinalIgnoreCase)) ?? models[0];

        Console.WriteLine($"推荐: {recommended.Name}");
        Console.WriteLine($"  大小: {recommended.DiskSizeMB} MB | 内存需求: {recommended.RecommendedMemoryMB} MB");
        Console.WriteLine();

        Console.WriteLine("可选模型：");
        for (int i = 0; i < models.Count; i++)
        {
            var m = models[i];
            var tag = m.Version == recommended.Version ? " ★推荐" : "";
            Console.WriteLine($"  [{i + 1}] {m.Name} ({m.DiskSizeMB}MB){tag}");
        }
        Console.WriteLine("  [J] Jina v5 Omni (多模态嵌入, 推荐 — AI自动下载)");
        Console.WriteLine("  [Enter] = 推荐模型");
        Console.WriteLine();

        var choice = ReadLine("选择编号 / J / 回车");
        if (choice?.ToUpperInvariant() == "J")
        {
            await DownloadJinaModelAsync(ct).ConfigureAwait(false);
            return true;
        }

        LocalModelInfo selectedModel;
        if (string.IsNullOrWhiteSpace(choice) || !int.TryParse(choice, out var idx) || idx < 1 || idx > models.Count)
        {
            Console.WriteLine("  ⏭️  使用推荐模型");
            selectedModel = recommended;
        }
        else
        {
            selectedModel = models[idx - 1];
        }

        Console.WriteLine();
        Console.WriteLine($"📥 下载 {selectedModel.Name} ({selectedModel.DiskSizeMB} MB)...");

        var progress = new Progress<ModelDownloadProgress>(p =>
        {
            Console.Write($"\r   {p.Percent:F1}% ({p.DownloadedBytes / 1024.0 / 1024.0:F1}/{p.TotalBytes / 1024.0 / 1024.0:F1} MB)");
        });

        try
        {
            var modelsDir = GetModelsDir();
            var path = await _modelDownloader.DownloadAsync(selectedModel, modelsDir, progress, ct).ConfigureAwait(false);
            Console.WriteLine($"\n  ✓ 下载完成: {path}");

            _options.AI.GetLayerConfig("L0").GetType().GetProperty("Provider")?.SetValue(_options.AI.GetLayerConfig("L0"), "local");
            _options.AI.GetLayerConfig("L0").GetType().GetProperty("Model")?.SetValue(_options.AI.GetLayerConfig("L0"), selectedModel.Version);
            _isDirty = true;

            Console.WriteLine();
            Console.WriteLine("  ✓ L0 已配置为本地 ONNX 模型");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n  ✗ 下载失败: {ex.Message}");
            Console.WriteLine("  回退到云端 API 选择...");
            Console.WriteLine();
            return false;
        }
    }

    private async Task ConfigureLocalModeAsync(string layerName, HardwareInfo hwInfo, CancellationToken ct)
    {
        var layer = layerName switch { "L0" => ModelLayer.L0, "L1" => ModelLayer.L1, "L2" => ModelLayer.L2, _ => ModelLayer.L1 };
        var models = LocalModelRegistry.GetByLayer(layer).ToList();

        if (models.Count == 0)
        {
            Console.WriteLine("  ⚠️  该层暂无可用本地模型");
            return;
        }

        var recommended = LocalModelRegistry.SelectBestModel(hwInfo.MemoryMB, layer);

        Console.WriteLine();
        Console.WriteLine($"推荐模型: {recommended.Name} ({recommended.DiskSizeMB} MB)");
        Console.WriteLine();
        Console.WriteLine("可选模型：");
        for (int i = 0; i < models.Count; i++)
        {
            var m = models[i];
            var isRec = m.Version == recommended.Version ? " (推荐)" : "";
            var engineTag = hwInfo.RecommendedEngine == m.EngineType ? " ⭐适配" : "";
            Console.WriteLine($"  [{i + 1}] {m.Name}{isRec}{engineTag}");
            Console.WriteLine($"      {m.EngineType.ToUpper()} | {m.DiskSizeMB} MB | {m.Description}");
        }
        Console.WriteLine();

        var modelChoice = ReadLine("选择模型编号");
        if (string.IsNullOrWhiteSpace(modelChoice) || !int.TryParse(modelChoice, out var modelIdx) || modelIdx < 1 || modelIdx > models.Count)
        {
            Console.WriteLine("  ⏭️  使用推荐模型");
            var recIdx = models.FindIndex(m => m.Version == recommended.Version);
            modelIdx = recIdx >= 0 ? recIdx + 1 : 1;
        }

        var selectedModel = models[modelIdx - 1];

        Console.WriteLine();
        Console.WriteLine($"已选择: {selectedModel.Name}");
        Console.WriteLine($"  格式: {selectedModel.EngineType.ToUpper()}");
        Console.WriteLine($"  大小: {selectedModel.DiskSizeMB} MB");
        Console.WriteLine();

        var downloadChoice = ReadChoice("是否立即下载？(Y/n)", "Y", new[] { "Y", "y", "n", "N" });
        if (downloadChoice is "Y" or "y")
        {
            Console.WriteLine();
            Console.WriteLine($"📥 正在下载 {selectedModel.Name}...");
            Console.WriteLine("   如果外网下载失败，将自动使用 hf-mirror.com 国内镜像");
            Console.WriteLine();

            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                Console.Write($"\r   {p.Percent:F1}% ({p.DownloadedBytes / 1024.0 / 1024.0:F1}/{p.TotalBytes / 1024.0 / 1024.0:F1} MB)");
            });

            try
            {
                var modelsDir = GetModelsDir();
                var path = await _modelDownloader.DownloadAsync(selectedModel, modelsDir, progress, ct).ConfigureAwait(false);
                Console.WriteLine($"\n  ✓ 下载完成: {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n  ✗ 下载失败: {ex.Message}");
                Console.WriteLine("  提示: 可稍后使用 'ltai model download' 命令重试");
            }
        }

        var layerConfig = _options.AI.GetLayerConfig(layerName);
        layerConfig.GetType().GetProperty("Provider")?.SetValue(layerConfig, "local");
        layerConfig.GetType().GetProperty("Model")?.SetValue(layerConfig, selectedModel.Version);
        _isDirty = true;

        SaveLocalModelConfig(layerName, selectedModel);
    }

    private async Task DownloadJinaModelAsync(CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("━━━ Jina Embeddings v5 Omni (多模态) ━━━");
        Console.WriteLine();
        Console.WriteLine("选择模型变体：");
        Console.WriteLine("  [1] jina-embeddings-v5-omni-small (768维, ~500MB, 推荐)");
        Console.WriteLine("     支持文本 + 图像 + 音频嵌入，适合 8GB+ 设备");
        Console.WriteLine("  [2] jina-embeddings-v5-omni-nano (512维, ~200MB, 轻量)");
        Console.WriteLine("     支持文本嵌入，适合 4GB+ / 边缘设备");
        Console.WriteLine("  [Enter] = 1 (推荐)");
        Console.WriteLine();

        var choice = ReadLine("选择 (1/2) 或回车");
        var isNano = choice == "2";
        var variant = isNano ? "nano" : "small";
        var dim = isNano ? 512 : 768;
        var modelName = $"jina-embeddings-v5-omni-{variant}";
        var hfRepo = "jinaai/jina-embeddings-v5-omni";
        var variantPath = isNano ? "onnx_nano" : "onnx_small";

        Console.WriteLine();
        Console.WriteLine($"📥 正在下载 {modelName}...");

        var cacheDir = Path.Combine(AppContext.BaseDirectory, ".livingtree", "models", "embedding");
        var onnxDir = Path.Combine(cacheDir, "jina", modelName);
        var onnxPath = Path.Combine(onnxDir, "model.onnx");
        var tokenizerPath = Path.Combine(onnxDir, "tokenizer.json");

        try
        {
            Directory.CreateDirectory(onnxDir);
            await DownloadWithMirrorAsync(
                $"https://huggingface.co/{hfRepo}/resolve/main/{variantPath}/model.onnx",
                onnxPath, ct);
            await DownloadWithMirrorAsync(
                $"https://huggingface.co/{hfRepo}/resolve/main/tokenizer.json",
                tokenizerPath, ct);

            Console.WriteLine($"  ✓ {modelName} 下载完成");
            Console.WriteLine($"  模型: {onnxPath}");
            Console.WriteLine($"  Tokenizer: {tokenizerPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ 下载失败: {ex.Message}");
            Console.WriteLine("  请稍后重试或选择其他模型");
            return;
        }

        _options.AI.GetLayerConfig("L0").GetType().GetProperty("Provider")?.SetValue(_options.AI.GetLayerConfig("L0"), "jina");
        _options.AI.GetLayerConfig("L0").GetType().GetProperty("Model")?.SetValue(_options.AI.GetLayerConfig("L0"), modelName);
        typeof(LTAIOptions).GetProperty("Vector")?.SetValue(_options, new VectorConfig { Dimension = dim, Backend = "jina-onnx" });
        _isDirty = true;

        Console.WriteLine();
        Console.WriteLine("  ✓ Jina L0 配置完成");
    }

    private static async Task DownloadWithMirrorAsync(string hfUrl, string savePath, CancellationToken ct)
    {
        // Try hf-mirror.com first (domestic CDN), fallback to huggingface.co
        var urls = new[]
        {
            hfUrl.Replace("huggingface.co", "hf-mirror.com"),
            hfUrl
        };

        Exception? lastEx = null;
        foreach (var url in urls)
        {
            try
            {
                var response = await _hfClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                Directory.CreateDirectory(Path.GetDirectoryName(savePath) ?? throw new InvalidOperationException("Download save path has no parent directory"));
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = File.Create(savePath);
                await stream.CopyToAsync(file, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) { lastEx = ex; }
        }

        throw lastEx ?? new IOException($"Failed to download: {hfUrl}");
    }

    private void SaveLocalModelConfig(string layerName, LocalModelInfo model)
    {
        var configDir = Path.GetDirectoryName(_configPath) ?? ".";
        var configPath = Path.Combine(configDir, $"local_{layerName.ToLower()}.json");
        var config = new { Version = model.Version, EngineType = model.EngineType, Layer = layerName, DownloadedAt = DateTime.UtcNow };
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json);
    }

    private string GetModelsDir()
    {
        var rootDir = FindRootDirectory(AppContext.BaseDirectory, "models");
        if (rootDir != null)
            return Path.Combine(rootDir, "models");
        return Path.Combine(AppContext.BaseDirectory, "models");
    }

    private string? ReadLine(string prompt)
    {
        Console.Write($"{prompt}: ");
        return Console.ReadLine()?.Trim();
    }

    private string ReadChoice(string prompt, string defaultValue, string[] validChoices)
    {
        while (true)
        {
            Console.Write($"{prompt} [{defaultValue}]: ");
            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input))
                return defaultValue;

            if (validChoices.Contains(input, StringComparer.OrdinalIgnoreCase))
                return input;

            Console.WriteLine($"⚠️  无效选择，请输入: {string.Join("/", validChoices)}");
        }
    }

    private string ReadSecret(string prompt)
    {
        Console.Write($"{prompt}: ");
        var key = "";
        while (true)
        {
            var keyInfo = Console.ReadKey(intercept: true);
            if (keyInfo.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }
            if (keyInfo.Key == ConsoleKey.Backspace)
            {
                if (key.Length > 0)
                {
                    key = key[..^1];
                    Console.Write("\b \b");
                }
            }
            else
            {
                key += keyInfo.KeyChar;
                Console.Write("*");
            }
        }
        return key;
    }

    private static string GetProviderEnvVar(string providerName, string layerName)
    {
        var p = providerName.ToUpperInvariant();
        if (p == "DEEPSEEK") return "DEEPSEEK_API_KEY";
        if (p == "SILICONFLOW") return "SILICONFLOW_API_KEY";
        if (p == "ALIYUN") return "DASHSCOPE_API_KEY";
        if (p == "OPENAI") return "OPENAI_API_KEY";
        if (p == "ANTHROPIC") return "ANTHROPIC_API_KEY";
        return $"{p}_API_KEY";
    }

    private static void SetPersistentEnvVar(string name, string value)
    {
        try
        {
            Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
        }
        catch
        {
            Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
        }
    }

    private async Task<bool> ValidateApiKeyAsync(string endpoint, string apiKey, string model, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(apiKey))
            return false;

        try
        {
            var client = _httpClientFactory?.CreateClient() ?? new HttpClient();
            var response = await client.PostAsync(
                $"{endpoint.TrimEnd('/')}/chat/completions",
                new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        model,
                        messages = new[] { new { role = "user", content = "Hi" } },
                        max_tokens = 5
                    }),
                    Encoding.UTF8,
                    "application/json"),
                ct);

            return response.IsSuccessStatusCode;
        }
        catch { /* intentional: cleanup may fail */ return false; }
    }

    private static HardwareInfo DetectHardwareCapabilities()
    {
        var memMB = LocalModelRegistry.DetectAvailableMemoryMB();
        var cores = Environment.ProcessorCount;
        bool hasGpu = false;
        string gpuName = "无";
        bool hasNpu = false;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var p = new Process();
                p.StartInfo = new ProcessStartInfo("powershell", "-Command \"Get-PnpDevice -Class Display | Where-Object Status -eq 'OK' | Select-Object -ExpandProperty FriendlyName\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                p.Start();
                var gpuOutput = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                if (!string.IsNullOrEmpty(gpuOutput))
                {
                    hasGpu = true;
                    gpuName = gpuOutput.Split('\n').First().Trim();
                }

                using var p2 = new Process();
                p2.StartInfo = new ProcessStartInfo("powershell", "-Command \"Get-PnpDevice -Class ComputingAccelerator | Where-Object Status -eq 'OK' | Select-Object -ExpandProperty FriendlyName\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                p2.Start();
                var npuOutput = p2.StandardOutput.ReadToEnd().Trim();
                p2.WaitForExit(3000);
                hasNpu = !string.IsNullOrEmpty(npuOutput);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                hasGpu = File.Exists("/dev/dri/renderD128") || File.Exists("/dev/nvidia0");
                if (hasGpu)
                {
                    try
                    {
                        var lspci = Process.Start(new ProcessStartInfo("lspci", "-d ::0300") { RedirectStandardOutput = true, UseShellExecute = false });
                        gpuName = lspci?.StandardOutput.ReadLine()?.Trim() ?? "GPU";
                    }
                    catch { /* intentional: cleanup may fail */ }
                }
            }
        }
        catch { /* intentional: cleanup may fail */ }

        var recommendedEngine = hasNpu ? "onnx" : "gguf";

        return new HardwareInfo(cores, memMB, hasGpu, gpuName, hasNpu, recommendedEngine);
    }

    private static string? FindRootDirectory(string startDir, string markerDir)
    {
        var current = startDir;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, markerDir)))
                return current;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    private record HardwareInfo(int CpuCores, long MemoryMB, bool HasGpu, string GpuName, bool HasNpu, string RecommendedEngine);
}
