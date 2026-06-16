using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LTAI.AI.Evaluation;
using LTAI.Agent.Workflows;
using LTAI.Agent.Diagnostics;
using LTAI.Core.Configuration;
using LTAI.Hpo;
using LTAI.Hpo.Samplers;
using LTAI.Hpo.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Agent.Services;

public sealed class AutoTunerService : BackgroundService
{
    private readonly ILogger<AutoTunerService> _logger;
    private readonly LTAIOptions _options;
    private readonly YAMLWorkflowRegistry _registry;
    private readonly IServiceProvider _sp;

    public AutoTunerService(
        IOptions<LTAIOptions> options,
        YAMLWorkflowRegistry registry,
        IServiceProvider sp,
        ILogger<AutoTunerService> logger)
    {
        _options = options.Value;
        _registry = registry;
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var cfg = _options.AutoTune;
        if (cfg == null || !cfg.Enabled)
        {
            _logger.LogInformation("AutoTuner disabled");
            return;
        }

        _logger.LogInformation("AutoTuner running: {NTrials} trials", cfg.Trials);

        var dbPath = cfg.StorePath ?? Path.Combine(".livingtree", "hpo.db");
        var store = new SqliteStudyStore(dbPath);
        var study = new Study("auto_tune", new TpeSampler(cfg.Seed), store, direction: StudyDirection.Maximize);

        var evalDir = cfg.EvalDir ?? Path.Combine(_options.DataDirectory, "eval");
        var configDir = cfg.ConfigDir ?? Path.Combine(_options.DataDirectory, "ltai-workflows");

        await study.OptimizeAsync(async trial =>
        {
            var config = new DecisionTreeConfig
            {
                TopK = trial.SuggestInt("topK", 3, 15),
                ConfidenceMarginThreshold = trial.SuggestFloat("margin", 0.05f, 0.5f),
                MinTopScoreThreshold = trial.SuggestFloat("minScore", 0.1f, 0.7f),
                MinAcceptableScore = trial.SuggestFloat("minAccept", 0.02f, 0.2f),
                AmbiguousFallback = trial.SuggestCategorical("fallback", new[] { "all", "topK" }),
            };

            Directory.CreateDirectory(configDir);
            File.WriteAllText(Path.Combine(configDir, "decision-tree.json"),
                JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

            await Task.Delay(500, ct).ConfigureAwait(false);
            await _registry.ReloadAllAsync(ct).ConfigureAwait(false);

            var score = await RunEvalAsync(evalDir, ct).ConfigureAwait(false);

            _logger.LogInformation("Trial {N}: score={S:F4}", trial.Number, score);
            return score;

        }, cfg.Trials, ct).ConfigureAwait(false);

        if (study.BestParams != null)
        {
            // Write best config (archive)
            var bestPath = Path.Combine(configDir, "decision-tree.best.json");
            File.WriteAllText(bestPath, JsonSerializer.Serialize(
                new { study.BestParams, study.BestValue, GeneratedAt = DateTime.UtcNow },
                new JsonSerializerOptions { WriteIndented = true }));

            // Apply best params to active config
            var activePath = Path.Combine(configDir, "decision-tree.json");
            if (File.Exists(activePath))
            {
                var active = System.Text.Json.JsonSerializer.Deserialize<DecisionTreeConfig>(
                    await File.ReadAllTextAsync(activePath, ct).ConfigureAwait(false));
                if (active != null)
                {
                    if (study.BestParams.TryGetValue("topK", out var topK) && topK is JsonElement tk)
                        active.TopK = tk.GetInt32();
                    if (study.BestParams.TryGetValue("margin", out var m) && m is JsonElement mg)
                        active.ConfidenceMarginThreshold = (float)mg.GetDouble();
                    if (study.BestParams.TryGetValue("minScore", out var ms) && ms is JsonElement mn)
                        active.MinTopScoreThreshold = (float)mn.GetDouble();
                    if (study.BestParams.TryGetValue("fallback", out var fb) && fb is JsonElement f)
                        active.AmbiguousFallback = f.GetString() ?? "all";
                    File.WriteAllText(activePath, JsonSerializer.Serialize(active,
                        new JsonSerializerOptions { WriteIndented = true }));
                    await _registry.ReloadAllAsync(ct).ConfigureAwait(false);
                    _logger.LogInformation("Applied best config to {Path} (score={S:F4})", activePath, study.BestValue);
                }
            }
        }
    }

    private async Task<double> RunEvalAsync(string evalDir, CancellationToken ct)
    {
        if (!Directory.Exists(evalDir))
        {
            _logger.LogWarning("Eval dir not found: {D}", evalDir);
            return 0;
        }

        var harness = new EvalHarness();
        var items = new List<EvalItem>();
        foreach (var f in Directory.EnumerateFiles(evalDir, "*.json"))
        {
            try
            {
                var doc = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    await File.ReadAllTextAsync(f, ct).ConfigureAwait(false));
                if (doc != null && doc.TryGetValue("prompt", out var p)
                    && doc.TryGetValue("expected", out var e))
                {
                    items.Add(new EvalItem(p, string.Empty, e, Path.GetFileNameWithoutExtension(f)));
                }
            }
            catch
            {
                // non-critical, best-effort
            }
        }
        if (items.Count == 0) return 0;

        var checks = new List<EvalCheck>();
        checks.Add(async (item, c) =>
        {
            try
            {
                var chat = _sp.GetService(typeof(Microsoft.Extensions.AI.IChatClient))
                    as Microsoft.Extensions.AI.IChatClient;
                if (chat == null)
                    return new EvalCheckResult(false, "No IChatClient", "no_svc");
                var msgs = new[] { new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, item.Query) };
                var resp = await chat.GetResponseAsync(msgs, cancellationToken: c).ConfigureAwait(false);
                var text = resp.Text ?? string.Empty;
                var ok = text.Contains(item.Context ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                var preview = text.Length > 80 ? text[..80] + "..." : text;
                var reason = ok ? "OK" : string.Format("Expected '{0}', got: {1}", item.Context, preview);
                return new EvalCheckResult(ok, reason, "llm");
            }
            catch (Exception ex) { return new EvalCheckResult(false, ex.Message, "err"); }
        });

        var report = await harness.RunAsync(items, checks, cancellationToken: ct).ConfigureAwait(false);
        return report.Total > 0 ? (double)report.Passed / report.Total : 0;
    }
}