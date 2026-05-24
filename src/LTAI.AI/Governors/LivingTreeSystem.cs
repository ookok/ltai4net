using System.Runtime.CompilerServices;
using LTAI.Core.Configuration;
using LTAI.Core.Execution;
using LTAI.Core.Interfaces;
using LTAI.Core.Messaging;
using LTAI.Core.Models;
using LTAI.DNA;
using LTAI.Tools.Reasoning;
using LTAI.AI.Providers;
using LTAI.AI.Utilities;
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
    private readonly ICrossRunEvolutionStore? _evolutionStore;
    private readonly IVerifiableRegistry? _verifiableRegistry;
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
        CrossLevelDistiller? crossDistiller = null,
        ICrossRunEvolutionStore? evolutionStore = null,
        IVerifiableRegistry? verifiableRegistry = null)
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
        _evolutionStore = evolutionStore;
        _verifiableRegistry = verifiableRegistry;
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

        if (_evolutionStore != null)
        {
            var activeLessons = _evolutionStore.GetActiveLessons(10);
            if (activeLessons.Count > 0)
            {
                _logger.LogInformation("Cross-run evolution: loaded {Count} active lessons from prior runs",
                    activeLessons.Count);
            }
        }
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

        // L0: intent classification + knowledge graph shortcut
        var toolCount = _toolRegistry.ListTools().Count();
        if (_duplexRouter != null)
        {
            var routeResult = _duplexRouter.Route(query);
            if (routeResult.CanAnswerLocally)
            {
                yield return routeResult.LocalResponse;
                yield break;
            }

            if (routeResult.Route == "delegate_l2")
            {
                var ctxResult = await _context.ProcessAsync(new Handshake
                {
                    To = "context", Action = "preload",
                    Payload = new Dictionary<string, object?> { ["query"] = query },
                    ReplyTo = Guid.NewGuid().ToString("N")
                }, cancellationToken);
                var ctx = ctxResult.Payload?.GetValueOrDefault("context")?.ToString() ?? "";
                var toolHint = toolCount > 0
                    ? $"\n[System: You have {toolCount} tools available including web_search, shell_exec, http_get, filesystem_read, and more. Use tools when you need real-time information or external capabilities.]"
                    : "";
                var fullQuery = string.IsNullOrEmpty(ctx) ? query + toolHint : $"Context:\n{ctx}\n\nQuery: {query}{toolHint}";
                var teachingResult = await _duplexRouter.RequestL2ReasoningAsync(fullQuery, routeResult, cancellationToken);
                if (teachingResult != null)
                {
                    _duplexRouter.LearnFromL2(query, teachingResult);
                    _context.AddTurn(query, teachingResult.Answer);
                    yield return teachingResult.Answer;
                    yield break;
                }
            }
        }

        // L0 classify: fast vs deep
        var label = "general";
        string model;
        try
        {
            var inputResult = await _input.ProcessAsync(new Handshake
            {
                To = "input", Action = "process",
                Payload = new Dictionary<string, object?> { ["query"] = query }
            }, cancellationToken);
            label = inputResult.Payload?.GetValueOrDefault("label")?.ToString() ?? "deep";
        }
        catch { }

        model = label switch { "fast" or "reflex" => FlashModel, "deep" => DefaultModel, _ => DefaultModel };

        var messages = new List<ChatMessage>();
        var streamOptions = new ChatOptions
        {
            ModelId = model,
            Temperature = 0.3f,
            MaxOutputTokens = 4096,
            Tools = _toolRegistry.GetTools().ToList()
        };

        if (toolCount > 0)
        {
            var toolNames = string.Join("、", _toolRegistry.ListTools().Take(10));
            messages.Add(new ChatMessage(ChatRole.System,
                $"你可以使用以下工具: {toolNames} 等共 {toolCount} 个工具。" +
                "遇到不确定的事实性问题、需要实时数据、需要操作系统文件或执行命令时，请主动调用相应的工具后再回答。"));
        }

        messages.Add(new ChatMessage(ChatRole.User, query));

        if (toolCount > 0 && (label == "fast" || label == "reflex"))
        {
            messages.Insert(0, new ChatMessage(ChatRole.System,
                $"你可以使用以下工具: {string.Join("、", _toolRegistry.ListTools().Take(10))} 等共 {toolCount} 个。" +
                "重要规则: 1) 遇到需要实时信息、外部数据或事实核查的问题，必须先调用工具再回答。" +
                "2) 如果工具返回空结果或不确定信息，必须如实告知用户'未找到相关信息'，严禁编造或推测。" +
                "3) 回答中明确区分【工具返回的事实】和【你的推测】，推测必须标注'不确定'。"));

            var response = await _llm.GetResponseAsync(messages, streamOptions, cancellationToken);
            var text = response.Text ?? "";
            if (!string.IsNullOrEmpty(text))
                yield return text;
            yield break;
        }

        IAsyncEnumerable<ChatResponseUpdate> streamResponse;
        try { streamResponse = _llm.GetStreamingResponseAsync(messages, streamOptions, cancellationToken); }
        catch (Exception ex) { streamResponse = null!; _logger.LogError(ex, "Stream init failed"); }

        if (streamResponse == null) { yield return "Error connecting to provider."; yield break; }

        await foreach (var update in streamResponse)
        {
            // Yield thinking/reasoning tokens with a prefix marker
            foreach (var content in update.Contents)
            {
                if (content is TextReasoningContent rc && !string.IsNullOrEmpty(rc.Text))
                    yield return $"<thinking>{rc.Text}</thinking>";
            }

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
        var maxTokens = _options.Value.AI.MaxTokens > 0 ? _options.Value.AI.MaxTokens : 4096;
        var options = new ChatOptions { ModelId = model, Temperature = temperature, MaxOutputTokens = maxTokens, Tools = _toolRegistry.GetTools().ToList() };

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

                _evolutionStore?.RecordLesson(new EvolutionLesson
                {
                    Category = LessonCategory.ModelDegradation.ToString(),
                    Severity = 0.6f,
                    Summary = $"Model {model} failed and degraded to {fallbackModel}",
                    Mitigation = $"Use {fallbackModel} as fallback; monitor {model} error rate",
                    SourceRun = traceId,
                    SourceStage = "l2_response"
                });
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

        var messages = new List<ChatMessage> { new(ChatRole.User, iterativePrompt) };
        var sb = new System.Text.StringBuilder();
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

        // Fire-and-forget review: doesn't block the critical path
        var capturedResponse = response;
        var capturedPrompt = iterativePrompt;
        _ = Task.Run(async () =>
        {
            try
            {
                var reviewPrompt = $"Review this response for accuracy and completeness. If it needs improvement, provide the improved version:\n\n{capturedResponse}";
                var reviewOptions = new ChatOptions { ModelId = baseOptions.ModelId, Temperature = 0.1f, MaxOutputTokens = 2048 };
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var reviewed = await _llm.CompleteAsync(reviewPrompt, reviewOptions, cts.Token);
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
            catch (Exception ex) { _logger.LogDebug(ex, "Background review skipped"); }
        });

        return response;
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

                        _verifiableRegistry?.RegisterMeasurement(new NumericMeasurement
                        {
                            Key = "onnx_training_accuracy",
                            Condition = $"samples={result.TrainingSamples}",
                            Value = result.Accuracy,
                            SourceExperiment = "lts_periodic_training",
                            Domain = "intent_classifier",
                            IsVerified = true,
                            Provenance = result.OnnxPath
                        });

                        if (result.Accuracy < 0.5f)
                        {
                            _evolutionStore?.RecordLesson(new EvolutionLesson
                            {
                                Category = LessonCategory.QualityRegression.ToString(),
                                Severity = 0.5f,
                                Summary = $"ONNX training accuracy low ({result.Accuracy:F2})",
                                Mitigation = "Increase training samples or adjust hyperparameters",
                                SourceRun = result.OnnxPath,
                                SourceStage = "onnx_training"
                            });
                        }

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
