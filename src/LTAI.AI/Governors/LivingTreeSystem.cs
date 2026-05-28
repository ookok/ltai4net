using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using LTAI.AI.Interfaces;
using LTAI.AI.Governors.Pipeline;
using LTAI.Core.Configuration;
using LTAI.Core.Execution;
using LTAI.Core.Governors;
using LTAI.Core.Interfaces;
using LTAI.Core.Messaging;
using LTAI.Core.Models;
using LTAI.Core.System;
using LTAI.DNA;
using LTAI.Models;
using LTAI.Tools.Reasoning;
using LTAI.AI.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI.Governors;

public sealed class LivingTreeSystem : ILivingTreeSystem, IAsyncDisposable
{
    private readonly TaskJournal _journal;
    private readonly IChatClient _llm;
    private readonly AIToolRegistry _toolRegistry;
    private readonly ILogger<LivingTreeSystem> _logger;
    private readonly DNAOrchestrator? _dna;
    private readonly IOptions<LTAIOptions> _options;
    private readonly GovernorSet _gov;
    private readonly BackgroundWorkQueue _workQueue;
    private readonly ContextHub? _contextHub;
    private readonly CPSProcessingService? _cpsProcessor;
    private readonly ReActLoopOrchestrator? _reActOrchestrator;
    private readonly LTAI.AI.Providers.PrefixCacheStore? _prefixCache;
    private readonly TaskPipeline _taskPipeline = new(null!);

    private string DefaultModel => _options.Value.AI.L2.Model;
    private string FlashModel => _options.Value.AI.L1.Model;

    public SystemGuardian Guardian => _gov.Guardian;
    public SystemMode Mode => _gov.Guardian.Mode;
    public bool DNAEnabled => _dna != null;
    public DNAStatus? DNAStatus => _dna?.GetStatus();
    public InputGovernor InputGovernor => _gov.Input;
    public ContextGovernor ContextGovernor => _gov.Context;
    public RoutingGovernor RoutingGovernor => _gov.Routing;
    public IChatClient LLMClient => _llm;
    public TaskPipeline TaskPipeline => _taskPipeline;

    public LivingTreeSystem(
        TaskJournal journal,
        IChatClient llm,
        IOptions<LTAIOptions> options,
        GovernorSet gov,
        AIToolRegistry toolRegistry,
        ILogger<LivingTreeSystem> logger,
        DNAOrchestrator? dna = null,
        ReasoningOrchestrator? reasoning = null,
        BackgroundWorkQueue? workQueue = null,
        ContextHub? contextHub = null,
        CPSProcessingService? cpsProcessor = null,
        ReActLoopOrchestrator? reActOrchestrator = null,
        LTAI.AI.Providers.PrefixCacheStore? prefixCache = null)
    {
        _journal = journal;
        _llm = llm;
        _toolRegistry = toolRegistry;
        _logger = logger;
        _options = options;
        _gov = gov;
        _dna = dna;
        _workQueue = workQueue ?? new BackgroundWorkQueue();
        _contextHub = contextHub;
        _cpsProcessor = cpsProcessor;
        _reActOrchestrator = reActOrchestrator;
        _prefixCache = prefixCache;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _gov.Guardian.StartMonitoring(TimeSpan.FromSeconds(15));

        // Lock the immutable prefix for DeepSeek cache optimization
        if (_prefixCache != null && !_prefixCache.PrefixLocked)
        {
            var sysPrompt = "LTAI Agent OS V1.0 — 6-layer microkernel architecture";
            var toolsJson = string.Join(",", _toolRegistry.ListTools().Select(t => t.ToString()));
            _prefixCache.LockPrefix(sysPrompt, toolsJson);
        }

        _logger.LogInformation("LivingTreeSystem v6.0 initialized, DNA: {DNA}, CPS: {CPS}, Cache: {Cache}",
            _dna != null ? "enabled" : "disabled",
            _cpsProcessor != null ? "enabled" : "disabled",
            _prefixCache?.GetCacheStats() ?? "disabled");
    }

    // ========================================================================
    // ChatAsync — primary sync entry point
    // ========================================================================
    public async Task<string> ChatAsync(string query, CancellationToken cancellationToken = default)
    {
        var entry = _journal.Add(query);

        try
        {
            if (_gov.Guardian.Mode == SystemMode.LifeSupport)
            {
                _journal.Complete(entry, "emergency");
                return await _gov.Guardian.EmergencyChatAsync(query, cancellationToken).ConfigureAwait(false);
            }

            if (_journal.IsPaused)
            {
                _journal.Complete(entry, "paused");
                return "Journal is paused. Resume to continue.";
            }

            if (_dna != null)
            {
                var safetyCheck = await _dna.Safety.EvaluateAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!safetyCheck.Allowed)
                {
                    _journal.Complete(entry, $"blocked: {safetyCheck.BlockReason}");
                    _logger.LogWarning("DNA safety blocked query: {Reason}", safetyCheck.BlockReason);
                    return $"[Safety: {safetyCheck.BlockReason}]";
                }
            }

            if (_cpsProcessor != null)
            {
                _logger.LogTrace("[CPS-Chat] Routing via CPS");
                var cpsResult = await _cpsProcessor.ProcessAsync(query, cancellationToken).ConfigureAwait(false);
                if (cpsResult.Confidence >= 0.6f)
                {
                    _journal.Complete(entry, $"cps:{cpsResult.Route}");
                    _logger.LogInformation("[CPS-Chat] Fast path (confidence={Confidence})", cpsResult.Confidence);
                    return cpsResult.Response;
                }
                _logger.LogDebug("[CPS-Chat] Low confidence ({Confidence}), falling to L2 direct",
                    cpsResult.Confidence);
            }

            var response = await ProcessTypedAsync(GovernorInput.Create(query), cancellationToken).ConfigureAwait(false);
            _journal.Complete(entry, response.Response[..Math.Min(response.Response.Length, 500)]);

            var reply = response.Response;
            _workQueue.Enqueue(async ct => { try { await SilentSelfCheckAsync(reply); } catch (Exception ex) { _logger.LogWarning(ex, "SilentSelfCheck failed"); } }, "SilentSelfCheck");

            if (_dna != null && !string.IsNullOrEmpty(reply))
            {
                _workQueue.Enqueue(async ct =>
                {
                    try { await _dna.ProcessAsync(query, reply, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "DNA process failed"); }
                }, "DNA process");
            }

            return reply;
        }
        catch (Exception ex)
        {
            _journal.Fail(entry, ex.Message);
            _gov.Guardian.RecordError();
            _logger.LogError(ex, "Chat failed");
            throw;
        }
    }

    // ========================================================================
    // StreamChatAsync — streaming entry point
    // ========================================================================
    public async IAsyncEnumerable<string> StreamChatAsync(
        string query,
        string? modelOverride = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_gov.Guardian.Mode == SystemMode.LifeSupport)
        {
            yield return "[Emergency mode active]";
            yield break;
        }

        if (_dna != null)
        {
            var safetyCheck = await _dna.Safety.EvaluateAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!safetyCheck.Allowed)
            {
                _logger.LogWarning("DNA safety blocked stream query: {Reason}", safetyCheck.BlockReason);
                yield return $"[Safety: {safetyCheck.BlockReason}]";
                yield break;
            }
        }

        if (_cpsProcessor != null)
        {
            var cpsResult = await _cpsProcessor.ProcessAsync(query, cancellationToken).ConfigureAwait(false);
            if (cpsResult.Confidence >= 0.6f)
            {
                _logger.LogInformation("[CPS-Stream] Fast path (confidence={Confidence}, route={Route})",
                    cpsResult.Confidence, cpsResult.Route);
                yield return cpsResult.Response;
                yield break;
            }
        }

        string? hubContext = null;
        if (_contextHub != null)
        {
            try
            {
                var contextItems = _contextHub.Query(query, topK: 3);
                if (contextItems.Count > 0)
                {
                    hubContext = "[Context]\n" + string.Join("\n",
                        contextItems.Select(c => $"- [{c.Domain}/{c.Kind}] {c.Summary}"));
                }
            }
            catch { }
        }

        var prompt = query;
        if (hubContext != null)
            prompt = hubContext + "\n\n" + prompt;

        if (_reActOrchestrator != null)
        {
            _logger.LogDebug("[StreamReAct] CPS low confidence, falling back to ReAct loop");
            var meta = new MetaCognitiveAssessment
            {
                Familiarity = 0.5f,
                Novelty = 0.4f,
                Certainty = 0.4f,
                ShouldDelegate = true,
                DelegationReason = "CPS low confidence",
                Assessment = "Delegating to ReAct loop for complex task"
            };

            await foreach (var chunk in _reActOrchestrator.RunReActLoopAsync(
                prompt, DefaultModel, "deep", "", null, false, null, null, null, meta, false, 0, 1.0f, cancellationToken))
            {
                yield return chunk;
            }
            yield break;
        }

        _logger.LogDebug("[StreamL2Direct] Routing to L2 cloud streaming");
        var streamOptions = new ChatOptions { ModelId = DefaultModel, Temperature = 0.3f, MaxOutputTokens = 4096 };
        await foreach (var chunk in _llm.GetStreamingResponseAsync(prompt, streamOptions, cancellationToken))
        {
            yield return chunk.Text ?? "";
        }
    }

    public async IAsyncEnumerable<string> StreamWithModelAsync(
        string query, string modelId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var options = new ChatOptions { ModelId = modelId, Temperature = 0.3f, MaxOutputTokens = 4096 };
        await foreach (var chunk in _llm.GetStreamingResponseAsync(query, options, cancellationToken))
        {
            yield return chunk.Text ?? "";
        }
    }

    // ========================================================================
    // ProcessTypedAsync — CPS or L2 direct (simplified from 6-governor chain)
    // ========================================================================
    public async Task<GovernorOutput> ProcessTypedAsync(GovernorInput input, CancellationToken cancellationToken = default)
    {
        var traceId = input.TraceId;
        var query = input.Query;

        if (_journal.TryConsumeMessage(out var humanMessage) && humanMessage != null)
        {
            _logger.LogInformation("Human message injected: {Message}", humanMessage[..Math.Min(humanMessage.Length, 100)]);
            query = humanMessage;
        }

        if (_cpsProcessor != null)
        {
            var cpsResult = await _cpsProcessor.ProcessAsync(query, cancellationToken).ConfigureAwait(false);
            if (cpsResult.Confidence >= 0.6f)
            {
                _logger.LogInformation("[CPS-ProcessTyped] Fast path (confidence={Confidence})", cpsResult.Confidence);
                return GovernorOutput.Success(cpsResult.Response, traceId);
            }
            _logger.LogDebug("[CPS-ProcessTyped] Low confidence ({Confidence}), falling to L2 direct", cpsResult.Confidence);
        }

        _logger.LogDebug("[L2Direct] Routing to L2 cloud model");
        var maxTokens = _options.Value.AI.MaxTokens > 0 ? _options.Value.AI.MaxTokens : 4096;
        var l2Options = new ChatOptions { ModelId = DefaultModel, Temperature = 0.3f, MaxOutputTokens = maxTokens };
        var l2Result = await _llm.GetResponseAsync(query, l2Options, cancellationToken).ConfigureAwait(false);
        var response = l2Result.Text ?? "";

        if (_dna != null)
        {
            try
            {
                var outputSafety = await _dna.Safety.EvaluateOutputAsync(response, cancellationToken).ConfigureAwait(false);
                if (!outputSafety.Allowed)
                    return GovernorOutput.Blocked(outputSafety.BlockReason ?? "Blocked by DNA safety");
            }
            catch (Exception ex) { _logger.LogDebug(ex, "DNA output safety skipped"); }
        }

        return GovernorOutput.Success(response, traceId);
    }

    private async Task SilentSelfCheckAsync(string response)
    {
        try { await _gov.Output.SilentSelfCheckAsync(response); }
        catch (Exception ex) { _logger.LogDebug(ex, "Silent self-check skipped"); }
    }

    public async ValueTask DisposeAsync()
    {
        await _workQueue.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
