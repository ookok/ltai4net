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
using LTAI.Core.Setup;

namespace LTAI.Core.Setup;

public class InteractiveSetupWizard
{
    private readonly string _configPath;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ModelDownloader _modelDownloader;
    private LTAIOptions _options;
    private bool _isDirty;

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

        await ConfigureLayerAsync("L0", "Embedding 层 (向量检索)", hwInfo, cancellationToken);
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
                return JsonSerializer.Deserialize<LTAIOptions>(json) ?? new LTAIOptions();
            }
            catch
            {
                return new LTAIOptions();
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        return new LTAIOptions();
    }

    private void SaveConfig()
    {
        var json = JsonSerializer.Serialize(_options, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_configPath, json);
    }

    private async Task ConfigureLayerAsync(string layerName, string description, HardwareInfo hwInfo, CancellationToken ct)
    {
        Console.WriteLine($"━━━ {layerName}: {description} ━━━");
        Console.WriteLine();
        Console.WriteLine("选择模式：");
        Console.WriteLine("  [1] API 模式 (云端模型，能力强)");
        Console.WriteLine("  [2] 本地模式 (离线运行，隐私安全)");
        Console.WriteLine("  [Enter] 跳过，保持当前配置");
        Console.WriteLine();

        var choice = ReadLine("选择 (1/2) 或回车");
        if (string.IsNullOrWhiteSpace(choice))
        {
            Console.WriteLine($"  ⏭️  跳过 {layerName} 配置");
            Console.WriteLine();
            return;
        }

        if (choice == "1")
        {
            await ConfigureApiModeAsync(layerName, ct);
        }
        else if (choice == "2")
        {
            await ConfigureLocalModeAsync(layerName, hwInfo, ct);
        }
        else
        {
            Console.WriteLine($"  ⏭️  跳过 {layerName} 配置");
        }
        Console.WriteLine();
    }

    private async Task ConfigureApiModeAsync(string layerName, CancellationToken ct)
    {
        var registry = new ProviderRegistry();
        var providers = registry.AllProviders.ToList();

        Console.WriteLine();
        Console.WriteLine("选择 API 提供商：");
        Console.WriteLine("  ─── 云端提供商 ───");
        var cloudProviders = new List<(int Index, string Name)>();
        var localProviders = new List<(int Index, string Name)>();
        
        for (int i = 0; i < providers.Count; i++)
        {
            var p = providers[i];
            var url = registry.GetBaseUrl(p)!;
            var model = registry.GetDefaultModel(p)!;
            var isLocal = url.Contains("localhost") || url.Contains("127.0.0.1") || url.Contains("0.0.0.0") || url.Contains(".local");
            
            if (isLocal)
                localProviders.Add((i + 1, p));
            else
                cloudProviders.Add((i + 1, p));
            
            var tag = isLocal ? "  [本地无Key]" : "";
            Console.WriteLine($"  [{i + 1,2}] {p,-16} {model,-36}{tag}");
        }
        Console.WriteLine();

        var providerChoice = ReadLine("选择提供商编号");
        if (string.IsNullOrWhiteSpace(providerChoice) || !int.TryParse(providerChoice, out var selectedIdx) || selectedIdx < 1 || selectedIdx > providers.Count)
        {
            Console.WriteLine("  ⚠️  无效选择，跳过 API 配置");
            return;
        }

        var selectedProvider = providers[selectedIdx - 1];
        var endpoint = registry.GetBaseUrl(selectedProvider)!;
        var defaultModel = registry.GetDefaultModel(selectedProvider)!;
        var isLocalProvider = endpoint.Contains("localhost") || endpoint.Contains("127.0.0.1") || endpoint.Contains("0.0.0.0") || endpoint.Contains(".local");

        Console.WriteLine();
        Console.WriteLine($"提供商: {selectedProvider}{(isLocalProvider ? " (本地, 无需 API Key)" : "")}");
        Console.WriteLine($"端点: {endpoint}");
        Console.WriteLine($"默认模型: {defaultModel}");
        Console.WriteLine();

        string apiKey;
        if (isLocalProvider)
        {
            Console.WriteLine("  ℹ️  本地提供商不需要 API Key");
            apiKey = "";
        }
        else
        {
            apiKey = ReadSecret("API Key");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Console.WriteLine("  ⚠️  未提供 API Key，跳过");
                return;
            }
        }

        var customModel = ReadLine($"模型名称 (默认: {defaultModel})");
        var chosenModel = string.IsNullOrWhiteSpace(customModel) ? defaultModel : customModel;

        _options.AI.Providers[selectedProvider] = new ProviderConfig
        {
            Endpoint = endpoint,
            ApiKey = apiKey,
            Model = chosenModel
        };

        var layerConfig = _options.AI.GetLayerConfig(layerName);
        layerConfig.GetType().GetProperty("Provider")!.SetValue(layerConfig, selectedProvider);
        layerConfig.GetType().GetProperty("Model")!.SetValue(layerConfig, chosenModel);
        _isDirty = true;

        Console.WriteLine();
        if (isLocalProvider)
        {
            Console.WriteLine($"  ✓ {layerName} 本地提供商已配置");
        }
        else if (await ValidateApiKeyAsync(endpoint, apiKey, chosenModel, ct))
            Console.WriteLine($"  ✓ {layerName} API 配置成功，连接正常");
        else
            Console.WriteLine($"  ⚠️ {layerName} API 配置已保存，但连接失败");
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
                var path = await _modelDownloader.DownloadAsync(selectedModel, modelsDir, progress, ct);
                Console.WriteLine($"\n  ✓ 下载完成: {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n  ✗ 下载失败: {ex.Message}");
                Console.WriteLine("  提示: 可稍后使用 'ltai model download' 命令重试");
            }
        }

        var layerConfig = _options.AI.GetLayerConfig(layerName);
        layerConfig.GetType().GetProperty("Provider")!.SetValue(layerConfig, "local");
        layerConfig.GetType().GetProperty("Model")!.SetValue(layerConfig, selectedModel.Version);
        _isDirty = true;

        SaveLocalModelConfig(layerName, selectedModel);
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
        catch
        {
            return false;
        }
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
                    catch { }
                }
            }
        }
        catch { }

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
