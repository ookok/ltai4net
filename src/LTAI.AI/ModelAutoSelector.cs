using System.Text.Json;
using LTAI.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI;

/// <summary>
/// Orchestrates automatic model selection for L2 and L3 tiers at startup,
/// with periodic re-evaluation.
///
/// Selection rules:
///   L1: Always from user configuration (required).
///   L2: Auto-selected unless user has explicitly configured it.
///   L3: Auto-selected; falls back to L1 if no suitable model found.
///
/// Results are persisted to <c>.livingtree/model-selections.json</c> for
/// cross-restart continuity and inspected by TUI/Desktop/CLI.
/// </summary>
public sealed class ModelAutoSelector
{
    private readonly ProviderRegistry _registry;
    private readonly ModelScoringEngine _scoring;
    private readonly IOptionsMonitor<LTAIOptions> _opts;
    private readonly ILogger<ModelAutoSelector> _logger;
    private readonly string _selectionsPath;

    public ModelAutoSelector(ProviderRegistry registry, ModelScoringEngine scoring,
        IOptionsMonitor<LTAIOptions> opts, ILogger<ModelAutoSelector> logger)
    {
        _registry = registry;
        _scoring = scoring;
        _opts = opts;
        _logger = logger;
        _selectionsPath = opts.CurrentValue.ResolveDataPath("model-selections.json");
    }

    /// <summary>
    /// Runs auto-selection for a specific provider. Returns the selection result.
    /// </summary>
    /// <param name="providerId">Provider ID, e.g. "deepseek".</param>
    /// <param name="configuredL1">User-configured L1 model short ID.</param>
    /// <param name="configuredL2">User-configured L2 model short ID, or null to auto-select.</param>
    /// <param name="configuredL3">User-configured L3 model short ID, or null to auto-select.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AutoSelectResult?> SelectAsync(
        string providerId,
        string? configuredL1,
        string? configuredL2,
        string? configuredL3,
        CancellationToken ct)
    {
        var provider = _registry.FindProvider(providerId);
        if (provider == null)
        {
            _logger.LogWarning("Provider '{ProviderId}' not found in registry", providerId);
            return null;
        }

        _logger.LogInformation("Auto-selecting models for {Provider} ({ModelCount} models available)",
            provider.Name, provider.Models.Length);

        var models = provider.Models;

        // L1: must be user-configured
        ModelInfo? l1 = null;
        if (!string.IsNullOrEmpty(configuredL1))
            l1 = provider.FindModel(configuredL1);
        if (l1 == null)
        {
            _logger.LogError("L1 model '{ConfiguredL1}' not found for provider {Provider}", configuredL1, providerId);
            return null;
        }

        // L2: auto-select or use configured
        ModelInfo? l2 = null, l2Alt = null;
        if (!string.IsNullOrEmpty(configuredL2))
        {
            l2 = provider.FindModel(configuredL2);
            if (l2 == null)
                _logger.LogWarning("Configured L2 model '{ConfiguredL2}' not found; auto-selecting instead", configuredL2);
        }
        if (l2 == null)
        {
            (l2, l2Alt) = _scoring.SelectBestPair(models, ModelTierRequirements.L2);
            if (l2 == null)
            {
                _logger.LogWarning("No model meets L2 requirements for {Provider} — L2 will fall back to L1", providerId);
                l2 = l1; // ultimate fallback
            }
        }

        // L3: auto-select, then fall back to L1
        ModelInfo? l3 = null;
        if (!string.IsNullOrEmpty(configuredL3))
        {
            l3 = provider.FindModel(configuredL3);
        }
        if (l3 == null)
        {
            (l3, _) = _scoring.SelectBestPair(models, ModelTierRequirements.L3);
        }
        var effectiveL3 = l3?.ShortId ?? l1.ShortId;

        var result = new AutoSelectResult(
            Provider: providerId,
            L1: l1.ShortId,
            L1Alt: null,
            L2: l2.ShortId,
            L2Alt: l2Alt?.ShortId,
            L3: l3?.ShortId, // null = reuse L1
            SelectedAt: DateTime.UtcNow);

        await PersistAsync(result, ct).ConfigureAwait(false);
        LogResult(result, l3 != null);
        return result;
    }

    /// <summary>
    /// Re-evaluates selection. Returns a new result only if significantly better
    /// (improvement exceeds <see cref="AutoSelectConfig.MinScoreImprovement"/>).
    /// </summary>
    public async Task<AutoSelectResult?> ReEvaluateAsync(AutoSelectResult current, CancellationToken ct)
    {
        var cfg = _opts.CurrentValue.AI.AutoSelect ?? new AutoSelectConfig();
        if (!cfg.Enabled) return null;

        var provider = _registry.FindProvider(current.Provider);
        if (provider == null) return null;

        var models = provider.Models;
        var (newL2, _) = _scoring.SelectBestPair(models, ModelTierRequirements.L2);
        var (newL3, _) = _scoring.SelectBestPair(models, ModelTierRequirements.L3);

        var changed = false;
        string? newL2Id = null, newL3Id = null;

        if (newL2 != null && !string.Equals(newL2.ShortId, current.L2, StringComparison.OrdinalIgnoreCase))
        {
            var oldScore = _scoring.Score(provider.FindModel(current.L2)!, ModelTierRequirements.L2);
            var newScore = _scoring.Score(newL2, ModelTierRequirements.L2);
            if ((newScore - oldScore) > cfg.MinScoreImprovement)
            {
                newL2Id = newL2.ShortId;
                changed = true;
                _logger.LogInformation("L2 upgrade: {Old}→{New} (score {OldScore:F3}→{NewScore:F3})",
                    current.L2, newL2Id, oldScore, newScore);
            }
        }

        if (newL3 != null && current.L3 != null &&
            !string.Equals(newL3.ShortId, current.L3, StringComparison.OrdinalIgnoreCase))
        {
            var oldScore = _scoring.Score(provider.FindModel(current.L3)!, ModelTierRequirements.L3);
            var newScore = _scoring.Score(newL3, ModelTierRequirements.L3);
            if ((newScore - oldScore) > cfg.MinScoreImprovement)
            {
                newL3Id = newL3.ShortId;
                changed = true;
                _logger.LogInformation("L3 upgrade: {Old}→{New} (score {OldScore:F3}→{NewScore:F3})",
                    current.L3, newL3Id, oldScore, newScore);
            }
        }

        if (!changed) return null;

        var updated = current with
        {
            L2 = newL2Id ?? current.L2,
            L2Alt = null,
            L3 = newL3Id, // null = reuse L1
            SelectedAt = DateTime.UtcNow,
        };
        await PersistAsync(updated, ct).ConfigureAwait(false);
        return updated;
    }

    /// <summary>Loads the persisted selection from disk, or null if not available.</summary>
    public AutoSelectResult? LoadPersisted()
    {
        try
        {
            if (!File.Exists(_selectionsPath)) return null;
            var json = File.ReadAllText(_selectionsPath);
            return JsonSerializer.Deserialize<AutoSelectResult>(json);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load model-selections.json");
            return null;
        }
    }

    private async Task PersistAsync(AutoSelectResult result, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(_selectionsPath);
            if (dir != null) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(result);
            await File.WriteAllTextAsync(_selectionsPath, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist model-selections.json");
        }
    }

    private void LogResult(AutoSelectResult r, bool l3Selected)
    {
        _logger.LogInformation(
            "Model selection for {Provider}: L1={L1}, L2={L2}{L2Alt}, L3={L3}{L3Note}",
            r.Provider, r.L1, r.L2,
            r.L2Alt != null ? $" (alt: {r.L2Alt})" : "",
            r.EffectiveL3,
            l3Selected ? "" : " (reuses L1)");
    }
}

/// <summary>
/// Hosted service that runs auto-selection at startup and periodically re-evaluates.
/// </summary>
public sealed class ModelAutoSelectHostedService : BackgroundService
{
    private readonly ModelAutoSelector _selector;
    private readonly ProviderRegistry _registry;
    private readonly IOptionsMonitor<LTAIOptions> _opts;
    private readonly ILogger<ModelAutoSelectHostedService> _logger;
    private static AutoSelectResult? s_latestResult;

    /// <summary>The most recent auto-selection result, or null if not yet run.</summary>
    public static AutoSelectResult? LatestResult => Volatile.Read(ref s_latestResult);

    public ModelAutoSelectHostedService(ModelAutoSelector selector, ProviderRegistry registry,
        IOptionsMonitor<LTAIOptions> opts, ILogger<ModelAutoSelectHostedService> logger)
    {
        _selector = selector;
        _registry = registry;
        _opts = opts;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Delay to allow DI to fully build
            await Task.Delay(100, stoppingToken).ConfigureAwait(false);

            var cfg = _opts.CurrentValue.AI.AutoSelect ?? new AutoSelectConfig();
            if (!cfg.Enabled)
            {
                _logger.LogInformation("Auto-select disabled — skipping");
                return;
            }

            // ProviderRegistry is already initialized by DI; proceed with selection.

            // Determine L1 from config (required)
            var l1Cfg = _opts.CurrentValue.AI.L1;
            if (l1Cfg == null || string.IsNullOrEmpty(l1Cfg.Model))
            {
                _logger.LogWarning("L1 model not configured — auto-select cannot run");
                return;
            }

            var providerId = ResolveProviderId(l1Cfg.Provider ?? _opts.CurrentValue.AI.DefaultProvider ?? "");
            if (providerId == null)
            {
                _logger.LogWarning("Unknown provider '{Provider}' — auto-select cannot run", l1Cfg.Provider);
                return;
            }

            var result = await _selector.SelectAsync(
                providerId,
                configuredL1: l1Cfg.Model,
                configuredL2: _opts.CurrentValue.AI.L2?.Model,
                configuredL3: _opts.CurrentValue.AI.L3?.Model,
                stoppingToken).ConfigureAwait(false);

            if (result != null)
            {
                // Store result for MultiProviderChatClient et al.
                Volatile.Write(ref s_latestResult, result);
            }

            // Periodic re-evaluation
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(cfg.RefreshIntervalMin), stoppingToken).ConfigureAwait(false);

                var current = Volatile.Read(ref s_latestResult);
                if (current == null) continue;
                var updated = await _selector.ReEvaluateAsync(current, stoppingToken).ConfigureAwait(false);
                if (updated != null)
                    Volatile.Write(ref s_latestResult, updated);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModelAutoSelectHostedService failed");
        }
    }

    /// <summary>
    /// Maps an LTAI service name (e.g. "DeepSeek", "deepseek-fast") to a models.dev provider ID.
    /// </summary>
    private static string? ResolveProviderId(string serviceName)
    {
        // Direct match
        var direct = AutoSelectProviderMap.FirstOrDefault(kv =>
            string.Equals(kv.Key, serviceName, StringComparison.OrdinalIgnoreCase));
        if (direct.Value != null) return direct.Value;

        // Partial match (e.g. "deepseek-fast" → "deepseek")
        foreach (var kv in AutoSelectProviderMap)
        {
            if (serviceName.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }
        return null;
    }

    /// <summary>
    /// Maps LTAI service names (from KnownKeys / config) to models.dev provider IDs.
    /// </summary>
    private static readonly Dictionary<string, string> AutoSelectProviderMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deepseek-fast"] = "deepseek",
        ["deepseek-pro"] = "deepseek",
        ["DeepSeek"] = "deepseek",
        ["deepseek"] = "deepseek",
        ["SiliconFlow"] = "siliconflow",
        ["siliconflow"] = "siliconflow",
        ["Aliyun(Qwen)"] = "alibaba",
        ["alibaba"] = "alibaba",
        ["Zhipu(GLM)"] = "zhipuai",
        ["zhipuai"] = "zhipuai",
        ["OpenAI"] = "openai",
        ["openai"] = "openai",
        ["Anthropic"] = "anthropic",
        ["anthropic"] = "anthropic",
        ["OpenRouter"] = "openrouter",
        ["openrouter"] = "openrouter",
        ["StepFun"] = "stepfun",
        ["stepfun"] = "stepfun",
    };
}
