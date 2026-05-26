using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.Core.Configuration;
using LTAI.Core.Messaging;
using LTAI.Core.Models;
using LTAI.Core.System;
using LTAI.DNA;
using LTAI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI.Governors.Pipeline;

public sealed record PreprocessingResult
{
    public bool IsBlocked { get; init; }
    public string? BlockMessage { get; init; }
    public bool IsCached { get; init; }
    public string? CachedResponse { get; init; }
    public string Label { get; init; } = "deep";
    public string Model { get; init; } = "";
    public string? ExtractedEntity { get; init; }
    public MetaCognitiveAssessment? MetaAssessment { get; init; }
    public string? MetaContext { get; init; }
    public string? Layer1Context { get; init; }
    public bool Layer1HighConfidence { get; init; }
    public string? PatternToolName { get; init; }
    public string? AutoSearchContext { get; init; }
    public string? Layer2Context { get; init; }
    public string? ContextMapContext { get; init; }
    public bool PatternMatched { get; init; }
    public bool IsFuzzyQuery { get; init; }
    public string? ClarifyMessage { get; init; }
    public string DateTag { get; init; } = "";
    public float BudgetRatio { get; init; }
    public int ToolCount { get; init; }
    public bool ShouldYieldEarly { get; init; }
    public string? HarnessContext { get; init; }
}

public sealed class QueryPreprocessingService
{
    private readonly InputGovernor _input;
    private readonly IChatClient _llm;
    private readonly DNAOrchestrator? _dna;
    private readonly IOptions<LTAIOptions> _options;
    private readonly SystemGuardian _guardian;
    private readonly AIToolRegistry _toolRegistry;
    private readonly MetaCognitiveLayer _metaCognition;
    private readonly QueryPatternRouter _patternRouter;
    private readonly L1PlanExecutor _planExecutor;
    private readonly PromptTemplateStore _prompts;
    private readonly ContextMap? _contextMap;
    private readonly ILogger _logger;
    private readonly HarnessEvolution? _harnessEvo;

    public QueryPreprocessingService(
        InputGovernor input,
        IChatClient llm,
        DNAOrchestrator? dna,
        IOptions<LTAIOptions> options,
        SystemGuardian guardian,
        AIToolRegistry toolRegistry,
        MetaCognitiveLayer metaCognition,
        QueryPatternRouter patternRouter,
        L1PlanExecutor planExecutor,
        PromptTemplateStore prompts,
        ContextMap? contextMap,
        ILogger logger,
        HarnessEvolution? harnessEvo = null)
    {
        _input = input;
        _llm = llm;
        _dna = dna;
        _options = options;
        _guardian = guardian;
        _toolRegistry = toolRegistry;
        _metaCognition = metaCognition;
        _patternRouter = patternRouter;
        _planExecutor = planExecutor;
        _prompts = prompts;
        _contextMap = contextMap;
        _logger = logger;
        _harnessEvo = harnessEvo;
    }

    public async Task<PreprocessingResult> PreprocessAsync(
        string query,
        ConcurrentDictionary<string, (string Response, DateTime Expiry)> queryCache,
        BAVTRouter budgetRouter,
        CancellationToken ct)
    {
        var result = new PreprocessingResult();

        if (_guardian.Mode == SystemMode.LifeSupport)
        {
            return result with { IsBlocked = true, BlockMessage = await _guardian.EmergencyChatAsync(query, ct).ConfigureAwait(false) };
        }

        var ai = _options.Value.AI;
        var unconfigured = new List<string>();
        if (!ai.L0.IsConfigured) unconfigured.Add("L0 (Embedding)");
        if (!ai.L1.IsConfigured) unconfigured.Add("L1 (Fast Model)");
        if (!ai.L2.IsConfigured) unconfigured.Add("L2 (Deep Model)");
        if (unconfigured.Count > 0)
        {
            return result with
            {
                IsBlocked = true,
                BlockMessage = $"[Model Not Configured] 以下模型层级未配置:\n  {string.Join("\n  ", unconfigured)}\n\n请在 Settings → LLM Config 中为每一层设置 Provider 和 Model。"
            };
        }

        if (_dna != null)
        {
            var safetyCheck = await _dna.Safety.EvaluateAsync(query, cancellationToken: ct).ConfigureAwait(false);
            if (!safetyCheck.Allowed)
            {
                return result with { IsBlocked = true, BlockMessage = $"[Safety blocked: {safetyCheck.BlockReason}]" };
            }
        }

        var shieldResult = PromptShield.Instance.SanitizeInput(query);
        if (!shieldResult.Passed)
        {
            _logger.LogWarning("PromptShield: injection blocked layer={Layer} violations={Violations}",
                shieldResult.Layer, string.Join(",", shieldResult.Violations));
            return result with
            {
                IsBlocked = true,
                BlockMessage = $"[安全防护] 检测到潜在提示注入攻击 ({shieldResult.Layer}: {string.Join(", ", shieldResult.Violations)})。请重新输入正常查询。"
            };
        }

        if (queryCache.TryGetValue(query, out var cached) && DateTime.UtcNow < cached.Expiry)
        {
            return result with { IsCached = true, CachedResponse = cached.Response, ShouldYieldEarly = true };
        }

        var patternResult = await _patternRouter.MatchAndExecuteAsync(query, ct).ConfigureAwait(false);
        string? layer1Context = null;
        bool layer1HighConfidence = false;
        string? patternToolName = null;

        if (patternResult.Matched)
        {
            layer1Context = $"【Layer1 自动执行工具: {patternResult.ToolName}】\n{patternResult.ContextMessage}";
            layer1HighConfidence = patternResult.Confidence >= 0.95f;
            patternToolName = patternResult.ToolName;
            _logger.LogInformation("Layer1 matched: tool={Tool} confidence={Conf:F2}",
                patternResult.ToolName, patternResult.Confidence);

            if (layer1HighConfidence && patternResult.ContextMessage != null)
            {
                return result with
                {
                    PatternMatched = true,
                    PatternToolName = patternToolName,
                    Layer1Context = layer1Context,
                    Layer1HighConfidence = true,
                    CachedResponse = patternResult.ContextMessage,
                    ShouldYieldEarly = true
                };
            }
        }

        var toolCount = _toolRegistry.ListTools().Count();
        var label = "general";
        string? extractedEntity = null;
        try
        {
            var inputResult = await _input.ProcessAsync(new Handshake
            {
                To = "input", Action = "process",
                Payload = new Dictionary<string, object?> { ["query"] = query }
            }, ct);
            label = inputResult.Payload?.GetValueOrDefault("label")?.ToString() ?? "deep";
            extractedEntity = (inputResult.Payload?.GetValueOrDefault("entity_root") as string)
                ?? (inputResult.Payload?.GetValueOrDefault("entity") as string);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "L0 intent classification failed, falling back to 'deep'");
        }

        var defaultModel = _options.Value.AI.L2.Model;
        var flashModel = _options.Value.AI.L1.Model;
        var model = label switch { "fast" or "reflex" => flashModel, "deep" => defaultModel, _ => defaultModel };

        var budgetRatio = budgetRouter.BudgetRatio;
        if (budgetRatio < 0.3f && label == "deep" && query.Length < 50)
        {
            model = flashModel;
            _logger.LogInformation("CostRouter: budget low ({Ratio:F2}), downgraded to Flash", budgetRatio);
        }

        string? harnessContext = null;
        if (_harnessEvo != null)
        {
            var harnessQuery = _harnessEvo.ApplyHarnessToQuery(query);
            if (harnessQuery != query)
                harnessContext = harnessQuery;
        }

        var localConfidence = layer1HighConfidence
            ? 0.95f
            : label switch { "fast" => 0.8f, "reflex" => 0.9f, _ => 0.5f };
        var metaAssessment = _metaCognition.Assess(query, localConfidence);
        string? metaContext = null;

        if (metaAssessment.ShouldDelegate)
        {
            if (label is "fast" or "reflex")
            {
                model = defaultModel;
                metaContext = _prompts.Render("meta_model_upgrade", new Dictionary<string, string>
                {
                    ["certainty"] = metaAssessment.Certainty.ToString("F2"),
                    ["reason"] = metaAssessment.DelegationReason,
                    ["model"] = defaultModel
                });
            }
            else
            {
                metaContext = _prompts.Render("meta_tool_recommend", new Dictionary<string, string>
                {
                    ["certainty"] = metaAssessment.Certainty.ToString("F2"),
                    ["reason"] = metaAssessment.DelegationReason
                });
            }
            _logger.LogInformation("MetaCognition: {Assessment} | Model={Model}", metaAssessment.Assessment, model);
        }

        var hasVaguePattern = query.Contains("怎么样") || query.Contains("如何评价") ||
            query.Contains("讲一下") || query.Contains("说说") || query.Contains("聊聊");
        var hasQuestionWord = query.Contains('？') || query.Contains('?') ||
            query.Contains("什么") || query.Contains("怎么") || query.Contains("为什么") ||
            query.Contains("谁") || query.Contains("哪里") || query.Contains("何时") ||
            query.Contains("多少") || query.Contains("几") || query.Contains("哪");
        var isFuzzyQuery = !patternResult.Matched && extractedEntity == null
            && metaAssessment.ShouldDelegate && label != "fast" && label != "reflex"
            && query.Length < 100 && (hasVaguePattern || !hasQuestionWord);
        if (isFuzzyQuery)
        {
            var clarify = await GenerateClarificationAsync(query, ct).ConfigureAwait(false);
            if (clarify != null)
            {
                return result with
                {
                    IsFuzzyQuery = true,
                    ClarifyMessage = "您的提问比较模糊，请问您是指以下哪种情况？\n\n" + clarify,
                    ShouldYieldEarly = true
                };
            }
        }

        var now = DateTime.Now;
        var dayNames = new[] { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };
        var dateTag = $"当前日期: {now:yyyy年M月d日} {dayNames[(int)now.DayOfWeek]}";

        string? autoSearchContext = null;
        if (label != "fast" && label != "reflex" && extractedEntity != null
            && toolCount > 0 && _toolRegistry.HasTool("web_search")
            && !layer1HighConfidence
            && patternResult.ToolName != "web_search")
        {
            try
            {
                var searchResult = await _toolRegistry.InvokeAsync("web_search",
                    new Dictionary<string, object?> { ["query"] = extractedEntity, ["maxResults"] = 5 },
                    ct);
                if (searchResult?.ToString() is { Length: > 0 } raw)
                {
                    int resultCount = 0;
                    try
                    {
                        using var doc = JsonDocument.Parse(raw);
                        resultCount = doc.RootElement.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
                    }
                    catch { }

                    autoSearchContext = resultCount == 0
                        ? _prompts.Render("auto_search_empty", new Dictionary<string, string> { ["query"] = query })
                        : _prompts.Render("auto_search_results", new Dictionary<string, string> { ["results"] = CompressToolResult(raw) });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Auto web_search failed");
            }
        }

        string? layer2Context = null;
        if (!patternResult.Matched
            && autoSearchContext == null
            && layer1Context == null
            && metaAssessment.ShouldDelegate
            && label != "fast" && label != "reflex"
            && toolCount > 0
            && budgetRatio > 0.2f)
        {
            try
            {
                var planResult = await _planExecutor.PlanAndExecuteAsync(
                    query, _llm, _toolRegistry, flashModel, ct).ConfigureAwait(false);
                if (planResult.Success && planResult.ContextMessage != null)
                {
                    layer2Context = planResult.ContextMessage;
                    _logger.LogInformation("Layer2 plan executed: {Count} tools", planResult.ToolsExecuted);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Layer2 planning exception");
            }
        }

        string? contextMapContext = null;
        if (_contextMap != null)
        {
            var map = _contextMap.Store.BuildContextMap();
            if (!string.IsNullOrWhiteSpace(map) && map.Split('\n').Length > 2)
            {
                contextMapContext = map;
                if (!string.IsNullOrWhiteSpace(patternToolName))
                    _contextMap.Store.RecordUse($"domain:{patternToolName}");
                if (!string.IsNullOrWhiteSpace(extractedEntity))
                    _contextMap.Store.RecordUse($"entity:{extractedEntity}");
            }
        }

        return result with
        {
            Label = label,
            Model = model,
            ExtractedEntity = extractedEntity,
            MetaAssessment = metaAssessment,
            MetaContext = metaContext,
            Layer1Context = layer1Context,
            Layer1HighConfidence = layer1HighConfidence,
            PatternToolName = patternToolName,
            AutoSearchContext = autoSearchContext,
            Layer2Context = layer2Context,
            ContextMapContext = contextMapContext,
            PatternMatched = patternResult.Matched,
            DateTag = dateTag,
            BudgetRatio = (float)budgetRatio,
            ToolCount = toolCount,
            HarnessContext = harnessContext
        };
    }

    private async Task<string?> GenerateClarificationAsync(string query, CancellationToken ct)
    {
        try
        {
            var prompt = $"用户的提问比较模糊：\"{query}\"\n\n请生成2-3个可能的澄清问题，帮助用户明确意图。每行一个问题，以数字开头。";
            var response = await _llm.GetResponseAsync(prompt, new ChatOptions { ModelId = _options.Value.AI.L1.Model, Temperature = 0.3f, MaxOutputTokens = 200 }, ct).ConfigureAwait(false);
            var text = response.Text;
            return !string.IsNullOrWhiteSpace(text) && text.Length > 10 ? text : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Clarification generation skipped");
            return null;
        }
    }

    private static string CompressToolResult(string raw)
    {
        if (raw.Length <= 800) return raw;
        return raw[..800] + "...";
    }
}
