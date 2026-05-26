using System.Runtime.CompilerServices;
using System.Text;
using LTAI.AI.Providers;
using LTAI.Core.Configuration;
using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using LTAI.Core.System;
using LTAI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI.Governors;

public sealed class ModelDispatchService
{
    private readonly IChatClient _llm;
    private readonly ProviderFanOutRace? _fanOut;
    private readonly ContextGovernor _contextGovernor;
    private readonly BackgroundWorkQueue _workQueue;
    private readonly SynapticMemory? _synapticMemory;
    private readonly BAVTRouter _bavtRouter;
    private readonly IOptions<LTAIOptions> _options;
    private readonly ILogger _logger;

    private string DefaultModel => _options.Value.AI.L2.Model;
    private string FlashModel => _options.Value.AI.L1.Model;

    private string GetDegradedModel(string model)
    {
        var chain = _options.Value.ModelPricing?.DegradationChain;
        if (chain != null && chain.TryGetValue(model, out var fallback))
            return fallback;
        return FlashModel;
    }

    public ModelDispatchService(
        IChatClient llm,
        IOptions<LTAIOptions> options,
        ILogger<ModelDispatchService> logger,
        ContextGovernor? contextGovernor = null,
        ProviderFanOutRace? fanOut = null,
        BackgroundWorkQueue? workQueue = null,
        SynapticMemory? synapticMemory = null,
        BAVTRouter? bavtRouter = null)
    {
        _llm = llm;
        _options = options;
        _logger = logger;
        _contextGovernor = contextGovernor!;
        _fanOut = fanOut;
        _workQueue = workQueue ?? new BackgroundWorkQueue();
        _synapticMemory = synapticMemory;
        _bavtRouter = bavtRouter ?? new BAVTRouter(100.0);
    }

    public async Task<string> DispatchAndRunAsync(
        string label,
        string fullPrompt,
        ChatOptions options,
        string model,
        string traceId,
        ICrossRunEvolutionStore? evolutionStore,
        CancellationToken ct)
    {
        try
        {
            return label switch
            {
                "fast" or "reflex" => (await _llm.GetResponseAsync(fullPrompt, options, ct)).Text ?? "",
                _ when _fanOut != null => (await _fanOut.RaceAsync(fullPrompt, maxConcurrent: 3, cancellationToken: ct)).Answer,
                _ => await CollaborativeChatAsync(fullPrompt, options, ct)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var fallbackModel = GetDegradedModel(model);
            if (fallbackModel != model)
            {
                _logger.LogWarning("Model {Model} failed, fallback={Fallback}: {Error}",
                    model, fallbackModel, ex.Message);
                options.ModelId = fallbackModel;
                options.Temperature = 0.3f;
                var fallbackResponse = (await _llm.GetResponseAsync(fullPrompt, options, ct)).Text ?? "";

                evolutionStore?.RecordLesson(new EvolutionLesson
                {
                    Category = LessonCategory.ModelDegradation.ToString(),
                    Severity = 0.6f,
                    Summary = $"Model {model} failed and degraded to {fallbackModel}",
                    Mitigation = $"Use {fallbackModel} as fallback; monitor {model} error rate",
                    SourceRun = traceId,
                    SourceStage = "l2_response"
                });

                return fallbackResponse;
            }
            else { throw; }
        }
    }

    private async Task<string> CollaborativeChatAsync(string prompt, ChatOptions baseOptions, CancellationToken ct)
    {
        var history = _contextGovernor.CompressHistory();
        var iterativePrompt = string.IsNullOrEmpty(history)
            ? prompt
            : $"Previous conversation:\n{history}\n\nCurrent query:\n{prompt}\n\nPlease provide a thorough, well-reasoned response.";

        var messages = new List<ChatMessage> { new(ChatRole.User, iterativePrompt) };
        var sb = new StringBuilder();
        string? lastModel = null;

        await foreach (var update in _llm.GetStreamingResponseAsync(messages, baseOptions, ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
                sb.Append(update.Text);
            lastModel ??= update.ModelId;
        }
        var response = sb.ToString();

        if (string.IsNullOrWhiteSpace(response))
            throw new InvalidOperationException("Empty streaming response");

        var capturedResponse = response;
        var capturedPrompt = iterativePrompt;
        _workQueue.Enqueue(async ct =>
        {
            try
            {
                var reviewPrompt = $"Review this response for accuracy and completeness. If it needs improvement, provide the improved version:\n\n{capturedResponse}";
                var reviewOptions = new ChatOptions { ModelId = baseOptions.ModelId, Temperature = 0.1f, MaxOutputTokens = 2048 };
                var reviewed = await _llm.CompleteAsync(reviewPrompt, reviewOptions, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(reviewed))
                {
                    _synapticMemory?.Store(new SynapticExperience
                    {
                        Type = SynapseType.Correction, Query = capturedPrompt, Response = reviewed,
                        Label = "reviewed", Confidence = 0.85f, Reward = 0.9f,
                        Metadata = $"model={baseOptions.ModelId},original_len={capturedResponse.Length}"
                    });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "LLM review background task failed"); }
        }, "LLM review");

        return response;
    }

    public async IAsyncEnumerable<string> LlmDecomposeAsync(
        IChatClient llm, string task, [EnumeratorCancellation] CancellationToken ct)
    {
        var prompt = $"""
            Break down the following task into numbered subtasks. Each subtask should be a single, actionable step.
            Return ONLY the numbered list, one per line. No explanations.

            Task: {task}
            """;

        List<string> results;
        try
        {
            var response = await llm.GetResponseAsync(prompt, cancellationToken: ct).ConfigureAwait(false);
            var text = response.Text ?? "";
            results = new List<string>();

            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 5 && (char.IsDigit(trimmed[0]) || trimmed[0] == '-' || trimmed[0] == '*'))
                    results.Add(trimmed);
            }

            if (results.Count == 0)
                results.Add(task);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM task decomposition failed, using original task");
            results = new List<string> { task };
        }

        foreach (var result in results)
            yield return result;
    }
}
