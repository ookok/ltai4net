using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LTAI.Core.Governors;

namespace LTAI.AI.Governors;

public record LocalLlmBootstrapConfig
{
    public string ModelDir { get; init; } = "";
    public string? PreferredVersion { get; init; } = null;
    public bool AutoDownloadIfMissing { get; init; } = true;
    public bool AutoUpdate { get; init; } = false;
    public int MaxDownloadRetries { get; init; } = 3;
}

public sealed class LocalLlmBootstrapService : IHostedService
{
    private readonly LocalLlmBootstrapConfig _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LocalLlmBootstrapService> _logger;
    private readonly IL1InferenceEngine _engine;
    private RwkvModelInfo? _selectedModel;

    public LocalLlmBootstrapService(
        LocalLlmBootstrapConfig config,
        IHttpClientFactory httpClientFactory,
        IL1InferenceEngine engine,
        ILogger<LocalLlmBootstrapService> logger)
    {
        _config = config;
        _httpClientFactory = httpClientFactory;
        _engine = engine;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_config.ModelDir))
        {
            _logger.LogWarning("Local LLM model directory not configured. Skipping bootstrap.");
            return;
        }

        Directory.CreateDirectory(_config.ModelDir);

        // 尝试读取用户配置
        var userConfig = LoadUserConfig();
        _selectedModel = SelectModelToUse(userConfig);
        
        _logger.LogInformation("🧠 Selected Local LLM: {ModelName} ({Version}, {EngineType})", 
            _selectedModel.Name, _selectedModel.Version, _selectedModel.EngineType);

        var extension = _selectedModel.EngineType == "gguf" ? "gguf" : "onnx";
        var modelPath = Path.Combine(_config.ModelDir, $"{_selectedModel.Version}.{extension}");
        var metaPath = Path.Combine(_config.ModelDir, ".meta.json");

        bool needsDownload = !File.Exists(modelPath);
        bool needsUpdate = _config.AutoUpdate && await CheckForUpdatesAsync(metaPath, cancellationToken);

        if (needsDownload || needsUpdate)
        {
            if (needsUpdate)
                _logger.LogInformation("🔄 New model version available. Upgrading...");
            else
                _logger.LogInformation("📥 Local LLM model missing. Starting automatic download...");

            try
            {
                await DownloadWithRetryAsync(_selectedModel.Url, modelPath, _selectedModel.Name, cancellationToken);
                await SaveMetaAsync(metaPath, _selectedModel, cancellationToken);

                _logger.LogInformation("✅ Local LLM download completed. Initializing engine...");
                await _engine.InitializeAsync(modelPath, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to download Local LLM. System will run without local LLM fallback.");
            }
        }
        else
        {
            _logger.LogInformation("✅ Local LLM model found ({Version}). Initializing...", _selectedModel.Version);
            await _engine.InitializeAsync(modelPath, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private LocalLlmUserConfig? LoadUserConfig()
    {
        var configPath = Path.Combine(Path.GetDirectoryName(_config.ModelDir) ?? ".", "local_llm.json");
        if (!File.Exists(configPath))
            return null;

        try
        {
            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<LocalLlmUserConfig>(json);
        }
        catch
        {
            return null;
        }
    }

    private RwkvModelInfo SelectModelToUse(LocalLlmUserConfig? userConfig)
    {
        // 1. 优先使用用户显式选择的版本
        if (userConfig != null && !string.IsNullOrEmpty(userConfig.PreferredVersion))
        {
            var explicitModel = RwkvModelRegistry.GetByVersion(userConfig.PreferredVersion);
            if (explicitModel != null)
                return explicitModel;
        }

        if (!string.IsNullOrEmpty(_config.PreferredVersion))
        {
            var explicitModel = RwkvModelRegistry.GetByVersion(_config.PreferredVersion);
            if (explicitModel != null)
                return explicitModel;
        }

        // 2. 根据内存自动推荐
        var availableMemoryMB = RwkvModelRegistry.DetectAvailableMemoryMB();
        var recommended = RwkvModelRegistry.SelectBestModel(availableMemoryMB, "zh");
        _logger.LogInformation("💻 Detected {MemoryMB}MB available memory. Recommending {ModelName}.", availableMemoryMB, recommended.Name);

        return recommended;
    }

    private async Task<bool> CheckForUpdatesAsync(string metaPath, CancellationToken ct)
    {
        if (!File.Exists(metaPath))
            return true;

        try
        {
            var metaJson = await File.ReadAllTextAsync(metaPath, ct);
            var meta = JsonSerializer.Deserialize<LocalLlmMeta>(metaJson);
            if (meta == null) return true;

            var currentModel = RwkvModelRegistry.GetByVersion(meta.Version);
            if (currentModel == null) return true;

            return _selectedModel != null && currentModel.Version != _selectedModel.Version;
        }
        catch
        {
            return true;
        }
    }

    private async Task SaveMetaAsync(string metaPath, RwkvModelInfo model, CancellationToken ct)
    {
        var meta = new LocalLlmMeta(model.Version, model.Name, DateTime.UtcNow);
        var json = JsonSerializer.Serialize(meta);
        await File.WriteAllTextAsync(metaPath, json, ct);
    }

    private async Task DownloadWithRetryAsync(string url, string path, string name, CancellationToken ct)
    {
        for (int i = 0; i < _config.MaxDownloadRetries; i++)
        {
            try
            {
                await DownloadFileAsync(url, path, name, ct);
                return;
            }
            catch (Exception ex) when (i < _config.MaxDownloadRetries - 1)
            {
                _logger.LogWarning(ex, "⚠️ Download attempt {Attempt}/{Max} failed. Retrying...", i + 1, _config.MaxDownloadRetries);
                await Task.Delay(2000 * (i + 1), ct);
            }
        }
    }

    private async Task DownloadFileAsync(string url, string path, string name, CancellationToken ct)
    {
        if (File.Exists(path))
        {
            _logger.LogInformation("Skipping {Name}, already exists.", name);
            return;
        }

        using var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var canReportProgress = totalBytes != -1L;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        var totalRead = 0L;
        int bytesRead;
        var lastReportTime = DateTime.UtcNow;

        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;

            if (canReportProgress && (DateTime.UtcNow - lastReportTime).TotalSeconds > 2)
            {
                var progress = (double)totalRead / totalBytes * 100;
                var mbRead = totalRead / 1024.0 / 1024.0;
                var totalMb = totalBytes / 1024.0 / 1024.0;
                _logger.LogInformation("📥 Downloading {Name}: {Progress:F1}% ({MbRead:F1}/{TotalMb:F1} MB)", name, progress, mbRead, totalMb);
                lastReportTime = DateTime.UtcNow;
            }
        }

        _logger.LogInformation("✅ {Name} downloaded successfully ({Size:F1} MB).", name, totalRead / 1024.0 / 1024.0);
    }

    private record LocalLlmMeta(string Version, string Name, DateTime DownloadedAt);
    private record LocalLlmUserConfig(string? PreferredVersion, string? EngineType, DateTime? DownloadedAt);
}
