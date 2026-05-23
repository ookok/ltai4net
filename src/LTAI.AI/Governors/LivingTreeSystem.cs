using System.Runtime.CompilerServices;
using LTAI.Core.Configuration;
using LTAI.Core.Execution;
using LTAI.Core.Interfaces;
using LTAI.Core.Messaging;
using LTAI.Core.Models;
using LTAI.DNA;
using LTAI.Tools.Reasoning;
using LTAI.AI.Providers;
using LTAI.AI.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.AI.Governors;

public sealed class LivingTreeSystem
{
    private readonly ICognitiveMesh _mesh;
    private readonly TaskJournal _journal;
    private readonly IChatClient _llm;
    private readonly ProviderFanOutRace? _fanOut;
    private readonly AIToolRegistry _toolRegistry;
    private readonly ILogger<LivingTreeSystem> _logger;
    private readonly DNAOrchestrator? _dna;
    private readonly IOptions<LTAIOptions> _options;

    private readonly InputGovernor _input;
    private readonly ContextGovernor _context;
    private readonly RoutingGovernor _routing;
    private readonly OutputGovernor _output;
    private readonly SelfGovernor _self;
    private readonly SystemGuardian _guardian;
    private readonly ReasoningOrchestrator? _reasoning;
    private readonly L1L2DuplexRouter? _duplexRouter;
    private readonly SynapticMemory? _synapticMemory;
    private readonly DreamCycle? _dreamCycle;

    private readonly BAVTRouter _bavtRouter = new(100.0);
    private readonly ERLLoop _erlLoop = new();
    private readonly ElasticMemoryOrchestrator _elasticMemory = new();
    private readonly StructuredReflectionEngine _reflectionEngine = new();
    private readonly CoEchoDetector _echoDetector = new();
    private readonly OTESelector _oteSelector;
    private readonly TaskPipeline _taskPipeline;
    private readonly AdaptiveDepthController? _depthController;
    private readonly TieredLoraManager? _tieredLora;
    private readonly CrossLevelDistiller? _crossDistiller;
    private int _requestCount;
    private const int TrainingInterval = 50;

    private string DefaultModel => _options.Value.AI.L2.Model;
    private string FlashModel => _options.Value.AI.L1.Model;

    private string GetDegradedModel(string model)
    {
        var chain = _options.Value.ModelPricing?.DegradationChain;
        if (chain != null && chain.TryGetValue(model, out var fallback))
            return fallback;
        return FlashModel;
    }

    public SystemGuardian Guardian => _guardian;
    public SystemMode Mode => _guardian.Mode;
    public bool DNAEnabled => _dna != null;
    public DNAStatus? DNAStatus => _dna?.GetStatus();
    public InputGovernor InputGovernor => _input;
    public ContextGovernor ContextGovernor => _context;
    public RoutingGovernor RoutingGovernor => _routing;
    public IChatClient LLMClient => _llm;
    public TaskPipeline TaskPipeline => _taskPipeline;
    public AdaptiveDepthController? DepthController => _depthController;
    public TieredLoraManager? TieredLora => _tieredLora;
    public CrossLevelDistiller? CrossDistiller => _crossDistiller;

    public LivingTreeSystem(
        ICognitiveMesh mesh,
        TaskJournal journal,
        IChatClient llm,
        IOptions<LTAIOptions> options,
        InputGovernor input,
        ContextGovernor context,
        RoutingGovernor routing,
        OutputGovernor output,
        SelfGovernor self,
        SystemGuardian guardian,
        AIToolRegistry toolRegistry,
        ILogger<LivingTreeSystem> logger,
        ProviderFanOutRace? fanOut = null,
        DNAOrchestrator? dna = null,
        ReasoningOrchestrator? reasoning = null,
        L1L2DuplexRouter? duplexRouter = null,
        SynapticMemory? synapticMemory = null,
        DreamCycle? dreamCycle = null,
        AdaptiveDepthController? depthController = null,
        TieredLoraManager? tieredLora = null,
        CrossLevelDistiller? crossDistiller = null)
    {
        _mesh = mesh;
        _journal = journal;
        _llm = llm;
        _fanOut = fanOut;
        _toolRegistry = toolRegistry;
        _logger = logger;
        _options = options;
        _input = input;
        _context = context;
        _routing = routing;
        _output = output;
        _self = self;
        _guardian = guardian;
        _dna = dna;
        _reasoning = reasoning;
        _duplexRouter = duplexRouter;
        _synapticMemory = synapticMemory;
        _dreamCycle = dreamCycle;
        _depthController = depthController;
        _tieredLora = tieredLora;
        _crossDistiller = crossDistiller;
        _taskPipeline = new TaskPipeline(_journal);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _mesh.RegisterAsync(_input, cancellationToken);
        await _mesh.RegisterAsync(_context, cancellationToken);
        await _mesh.RegisterAsync(_routing, cancellationToken);
        await _mesh.RegisterAsync(_output, cancellationToken);
        await _mesh.RegisterAsync(_self, cancellationToken);

        _guardian.StartMonitoring(TimeSpan.FromSeconds(15));
        _logger.LogInformation("LivingTreeSystem v6.0 initialized with 5 governors, DNA: {DNA}",
            _dna != null ? "enabled" : "disabled");
    }

    public async Task<string> ChatAsync(string query, CancellationToken cancellationToken = default)
    {
        var entry = _journal.Add(query);

        try
        {
            if (_guardian.Mode == SystemMode.LifeSupport)
            {
                _journal.Complete(entry, "emergency");
                return await _guardian.EmergencyChatAsync(query, cancellationToken);
            }

            if (_journal.IsPaused)
            {
                _journal.Complete(entry, "paused");
                return "Journal is paused. Resume to continue.";
            }

            if (_dna != null)
            {
                var safetyCheck = await _dna.Safety.EvaluateAsync(query, cancellationToken: cancellationToken);
                if (!safetyCheck.Allowed)
                {
                    _journal.Complete(entry, $"blocked: {safetyCheck.BlockReason}");
                    _logger.LogWarning("DNA safety blocked query: {Reason}", safetyCheck.BlockReason);
                    return $"[Safety: {safetyCheck.BlockReason}]";
                }
            }

            var response = await ProcessTypedAsync(GovernorInput.Create(query), cancellationToken);
            _journal.Complete(entry, response.Response[..Math.Min(response.Response.Length, 500)]);

            var reply = response.Response;
            _ = Task.Run(async () =>
            {
                try { await SilentSelfCheckAsync(reply); }
                catch (Exception ex) { _logger.LogDebug(ex, "Silent self-check failed"); }
            }, cancellationToken).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                    _logger.LogDebug(t.Exception, "Silent self-check background task failed");
            }, TaskContinuationOptions.OnlyOnFaulted);

            if (_dna != null && !string.IsNullOrEmpty(reply))
            {
                _ = Task.Run(async () =>
                {
                    try { await _dna.ProcessAsync(query, reply, cancellationToken); }
                    catch (Exception ex) { _logger.LogDebug(ex, "DNA background process failed"); }
                }, cancellationToken).ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                        _logger.LogDebug(t.Exception, "DNA background task failed");
                }, TaskContinuationOptions.OnlyOnFaulted);
            }

            return reply;
        }
        catch (Exception ex)
        {
            _journal.Fail(entry, ex.Message);
            _guardian.RecordError();
            _logger.LogError(ex, "Chat failed");
            throw;
        }
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_guardian.Mode == SystemMode.LifeSupport)
        {
            yield return await _guardian.EmergencyChatAsync(query, cancellationToken);
            yield break;
        }

        if (_dna != null)
        {
            var safetyCheck = await _dna.Safety.EvaluateAsync(query, cancellationToken: cancellationToken);
            if (!safetyCheck.Allowed)
            {
                yield return $"[Safety blocked: {safetyCheck.BlockReason}]";
                yield break;
            }
        }

        if (_duplexRouter != null)
        {
            var routeResult = _duplexRouter.Route(query);
            if (routeResult.CanAnswerLocally)
            {
                yield return routeResult.LocalResponse;
                yield break;
            }
        }

        var model = DefaultModel;
        try
        {
            var routingResult = await _routing.ProcessAsync(new Handshake
            {
                To = "routing", Action = "select_provider",
                Payload = new Dictionary<string, object?> { ["query"] = query }
            }, cancellationToken);
            model = routingResult.Payload?.GetValueOrDefault("model")?.ToString() ?? DefaultModel;
        }
        catch { }

        var streamOptions = new ChatOptions { ModelId = model, Temperature = 0.3f, MaxOutputTokens = 4096, Tools = _toolRegistry.GetTools().ToList() };
        var messages = new List<ChatMessage> { new(ChatRole.User, query) };

        IAsyncEnumerable<ChatResponseUpdate> streamResponse;
        try { streamResponse = _llm.GetStreamingResponseAsync(messages, streamOptions, cancellationToken); }
        catch (Exception ex) { streamResponse = null!; _logger.LogError(ex, "Stream init failed"); }

        if (streamResponse == null) { yield return "Error connecting to provider."; yield break; }

        await foreach (var update in streamResponse)
        {
            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
        }
    }

    public async IAsyncEnumerable<string> StreamWithModelAsync(
        string query, string modelId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var modelOptions = new ChatOptions { ModelId = modelId, Temperature = 0.3f, MaxOutputTokens = 4096, Tools = _toolRegistry.GetTools().ToList() };
        var modelMessages = new List<ChatMessage> { new(ChatRole.User, query) };

        IAsyncEnumerable<ChatResponseUpdate> modelStream;
        try { modelStream = _llm.GetStreamingResponseAsync(modelMessages, modelOptions, cancellationToken); }
        catch (Exception ex) { _logger.LogError(ex, "StreamWithModel init failed for {Model}", modelId); yield break; }
        if (modelStream == null) yield break;

        await foreach (var update in modelStream)
        {
            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
        }
    }

    public async Task<GovernorOutput> ProcessTypedAsync(GovernorInput input, CancellationToken cancellationToken = default)
    {
        var traceId = input.TraceId;
        var query = input.Query;

        if (_journal.TryConsumeMessage(out var humanMessage) && humanMessage != null)
        {
            _logger.LogInformation("Human message injected: {Message}", humanMessage[..Math.Min(humanMessage.Length, 100)]);
            query = humanMessage;
            input = GovernorInput.Create(query, traceId);
        }

        if (_dna != null)
        {
            try { await _dna.Consciousness.ProcessExperienceAsync(query, cancellationToken: cancellationToken); }
            catch (Exception ex) { _logger.LogDebug(ex, "DNA consciousness processing skipped"); }
        }

        var inputResult = await _input.ProcessAsync(new Handshake
        {
            To = "input", Action = "process",
            Payload = new Dictionary<string, object?> { ["query"] = query },
            ReplyTo = traceId
        }, cancellationToken);

        if (inputResult.Action == "reflex")
            return GovernorOutput.Reflex(inputResult.Payload?.GetValueOrDefault("command")?.ToString() ?? "", traceId);

        var label = inputResult.Payload?.GetValueOrDefault("label")?.ToString() ?? "deep";

        if (_duplexRouter != null)
        {
            var routeResult = _duplexRouter.Route(query);
            if (routeResult.CanAnswerLocally)
            {
                _erlLoop.RecordTrial(query[..Math.Min(query.Length, 60)], routeResult.LocalResponse, "l1_success", 0.8, true);
                return GovernorOutput.Success(routeResult.LocalResponse, traceId);
            }

            if (routeResult.Route == "delegate_l2")
            {
                var ctxResult = await _context.ProcessAsync(new Handshake
                {
                    To = "context", Action = "preload",
                    Payload = inputResult.Payload, ReplyTo = traceId
                }, cancellationToken);

                var ctx = ctxResult.Payload?.GetValueOrDefault("context")?.ToString() ?? "";
                var fullQuery = string.IsNullOrEmpty(ctx) ? query : $"Context:\n{ctx}\n\nQuery: {query}";
                var teachingResult = await _duplexRouter.RequestL2ReasoningAsync(fullQuery, routeResult, cancellationToken);
                if (teachingResult != null)
                {
                    _duplexRouter.LearnFromL2(query, teachingResult);
                    _context.AddTurn(query, teachingResult.Answer);
                    _duplexRouter.CacheResponse(query, teachingResult.Answer, "delegate_l2", "general", routeResult.Confidence);
                    _erlLoop.RecordTrial(query[..Math.Min(query.Length, 60)], teachingResult.Answer, "l2_teaching", 0.9, true);
                    return GovernorOutput.Success(teachingResult.Answer, traceId);
                }
            }
        }

        var contextResult = await _context.ProcessAsync(new Handshake
        {
            To = "context", Action = "preload",
            Payload = inputResult.Payload, ReplyTo = traceId
        }, cancellationToken);

        var preloadedContext = contextResult.Payload?.GetValueOrDefault("context")?.ToString() ?? "";
        _elasticMemory.Store($"ctx_{traceId}", preloadedContext[..Math.Min(preloadedContext.Length, 500)]);

        var routingResult = await _routing.ProcessAsync(new Handshake
        {
            To = "routing", Action = "select_provider",
            Payload = inputResult.Payload, ReplyTo = traceId
        }, cancellationToken);

        var model = routingResult.Payload?.GetValueOrDefault("model")?.ToString() ?? DefaultModel;
        var temperature = routingResult.Payload?.GetValueOrDefault("temperature") is float t ? t : 0.3f;
        var context = contextResult.Payload?.GetValueOrDefault("context")?.ToString() ?? "";
        var fullPrompt = string.IsNullOrEmpty(context) ? query : $"Context:\n{context}\n\nQuery: {query}";

        if (_bavtRouter.BudgetRatio < 0.1)
        {
            _logger.LogWarning("BAVT: budget nearly exhausted (ratio={Ratio:F2}), using fast path", _bavtRouter.BudgetRatio);
            var fastResp = await _llm.GetResponseAsync(fullPrompt, new ChatOptions { ModelId = FlashModel, Temperature = 0.3f }, cancellationToken);
            _erlLoop.RecordTrial(query[..Math.Min(query.Length, 60)], fastResp.Text ?? "", "budget_fast", 0.5, true);
            return GovernorOutput.Success(fastResp.Text ?? "", traceId);
        }

        string response;
        var options = new ChatOptions { ModelId = model, Temperature = temperature, MaxOutputTokens = 4096, Tools = _toolRegistry.GetTools().ToList() };

        try
        {
            response = label switch
            {
                "fast" or "reflex" => (await _llm.GetResponseAsync(fullPrompt, options, cancellationToken)).Text ?? "",
                _ when _fanOut != null => (await _fanOut.RaceAsync(fullPrompt, maxConcurrent: 3, cancellationToken: cancellationToken)).Answer,
                _ => await CollaborativeChatAsync(fullPrompt, options, cancellationToken)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var fallbackModel = GetDegradedModel(model);
            if (fallbackModel != model)
            {
                var reflection = _reflectionEngine.Reflect(model, ex.Message, 1);
                _logger.LogWarning("Model {Model} failed, reflection={Action} fallback={Fallback}: {Error}",
                    model, reflection.Action, fallbackModel, ex.Message);
                options.ModelId = fallbackModel;
                options.Temperature = 0.3f;
                response = (await _llm.GetResponseAsync(fullPrompt, options, cancellationToken)).Text ?? "";
            }
            else { throw; }
        }

        _bavtRouter.Spend(1.0);
        _erlLoop.RecordTrial(query[..Math.Min(query.Length, 60)], response[..Math.Min(response.Length, 100)], "l2_response", 0.7, true);
        _elasticMemory.Store($"lts_{traceId}", response[..Math.Min(response.Length, 200)]);
        _echoDetector.RecordResponse(model, response[..Math.Min(response.Length, 500)]);
        if (_taskPipeline.HasPending) _logger.LogDebug("TaskPipeline: {Count} pending tasks", _taskPipeline.GetStats()["pending"]);

        if (++_requestCount % TrainingInterval == 0 && _options.Value.AI.OnnxEnabled)
        {
            _ = Task.Run(() => TriggerPeriodicTraining());
        }

        if (_dna != null)
        {
            try
            {
                var outputSafety = await _dna.Safety.EvaluateOutputAsync(response, cancellationToken);
                if (!outputSafety.Allowed)
                    return GovernorOutput.Blocked(outputSafety.BlockReason);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "DNA output safety check skipped"); }
        }

        var outputResult = await _output.ProcessAsync(new Handshake
        {
            To = "output", Action = "review",
            Payload = new Dictionary<string, object?> { ["response"] = response },
            ReplyTo = traceId
        }, cancellationToken);

        _context.AddTurn(query, response);
        response = outputResult.Payload?.GetValueOrDefault("response")?.ToString() ?? response;

        _duplexRouter?.CacheResponse(query, response, label, "general", 0.7f);

        if (_reasoning != null && !string.IsNullOrEmpty(response))
        {
            try { response = await _reasoning.EnhanceResponse(query, response); }
            catch (Exception ex) { _logger.LogDebug(ex, "Reasoning enhancement skipped"); }
        }

        _synapticMemory?.Store(new SynapticExperience
        {
            Type = SynapseType.Interaction, Query = query, Response = response,
            Label = label, Confidence = 0.7f, Reward = 0.7f,
            Metadata = $"model={model},trace={traceId}"
        });

        _dreamCycle?.RecordInteraction();

        _ = Task.Run(async () =>
        {
            try { await _self.ProcessAsync(new Handshake
            {
                To = "self", Action = "start_trace",
                Payload = new Dictionary<string, object?> { ["trace_id"] = traceId }
            }, cancellationToken); }
            catch (Exception ex) { _logger.LogDebug(ex, "Self governor trace failed"); }
        }, cancellationToken);

        return GovernorOutput.Success(response, traceId);
    }

    private async Task<string> CollaborativeChatAsync(string prompt, ChatOptions baseOptions, CancellationToken ct)
    {
        var history = _context.CompressHistory();
        var iterativePrompt = string.IsNullOrEmpty(history)
            ? prompt
            : $"Previous conversation:\n{history}\n\nCurrent query:\n{prompt}\n\nPlease provide a thorough, well-reasoned response.";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));

        var response = await _llm.CompleteAsync(iterativePrompt, baseOptions, cts.Token);

        var reviewPrompt = $"Review this response for accuracy and completeness. If it needs improvement, provide the improved version:\n\n{response}";
        var reviewOptions = new ChatOptions { ModelId = baseOptions.ModelId, Temperature = 0.1f, MaxOutputTokens = 8192 };

        using var reviewCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reviewCts.CancelAfter(TimeSpan.FromSeconds(30));

        return await _llm.CompleteAsync(reviewPrompt, reviewOptions, reviewCts.Token);
    }

    private string RestartSystem()
    {
        _guardian.ResetErrors();
        _journal.Clear();
        _logger.LogInformation("System restarted: journal cleared, guardian reset");
        return "System restarted.";
    }

    private async Task SilentSelfCheckAsync(string response)
    {
        try { await _output.SilentSelfCheckAsync(response); }
        catch (Exception ex) { _logger.LogDebug(ex, "Silent self-check skipped"); }
    }

    /// Periodically triggers ONNX retraining from collected synaptic experiences.
    /// Called every ~50 requests as a background fire-and-forget task.
    private async Task TriggerPeriodicTraining()
    {
        try
        {
            var samples = _synapticMemory?.GetTrainingSamples(maxCount: TrainingInterval) ?? new();

            if (samples.Count >= 10)
            {
                // Train and export ONNX weights
                var synapticDir = Path.Combine(AppContext.BaseDirectory, "synaptic");
                var trainer = new SynapticTrainer(Path.Combine(synapticDir, "models"));
                var result = trainer.TrainIntentClassifier(samples);

                if (result.Success)
                {
                    var inference = new SynapticInference();
                    if (!string.IsNullOrEmpty(result.OnnxPath) && inference.LoadOnnxModel(result.OnnxPath))
                    {
                        _logger.LogInformation("ONNX retraining: accuracy={Acc:F2} samples={N} onnx={Path}",
                            result.Accuracy, result.TrainingSamples, result.OnnxPath);
                        inference.Dispose();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Periodic training skipped");
        }
    }
}
