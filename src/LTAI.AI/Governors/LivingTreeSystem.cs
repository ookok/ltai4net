using System.Runtime.CompilerServices;
using LTAI.Core.Configuration;
using LTAI.Core.Execution;
using LTAI.Core.Interfaces;
using LTAI.Core.Messaging;
using LTAI.Core.Models;
using LTAI.DNA;
using LTAI.Capability.Reasoning;
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
    private readonly CapabilityGovernor _capability;
    private readonly StorageGovernor _storage;
    private readonly OutputGovernor _output;
    private readonly CommunicationGovernor _communication;
    private readonly TaskGovernor _task;
    private readonly SelfGovernor _self;
    private readonly EvolutionGovernor _evolution;
    private readonly SystemGuardian _guardian;
    private readonly ReasoningOrchestrator? _reasoning;
    private readonly L1L2DuplexRouter? _duplexRouter;
    private readonly SynapticMemory? _synapticMemory;
    private readonly DreamCycle? _dreamCycle;

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

    public LivingTreeSystem(
        ICognitiveMesh mesh,
        TaskJournal journal,
        IChatClient llm,
        ProviderFanOutRace? fanOut,
        AIToolRegistry toolRegistry,
        ILogger<LivingTreeSystem> logger,
        IOptions<LTAIOptions> options,
        InputGovernor input,
        ContextGovernor context,
        RoutingGovernor routing,
        CapabilityGovernor capability,
        StorageGovernor storage,
        OutputGovernor output,
        CommunicationGovernor communication,
        TaskGovernor task,
        SelfGovernor self,
        EvolutionGovernor evolution,
        SystemGuardian guardian,
        DNAOrchestrator? dna = null,
        ReasoningOrchestrator? reasoning = null,
        L1L2DuplexRouter? duplexRouter = null,
        SynapticMemory? synapticMemory = null,
        DreamCycle? dreamCycle = null)
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
        _capability = capability;
        _storage = storage;
        _output = output;
        _communication = communication;
        _task = task;
        _self = self;
        _evolution = evolution;
        _guardian = guardian;
        _dna = dna;
        _reasoning = reasoning;
        _duplexRouter = duplexRouter;
        _synapticMemory = synapticMemory;
        _dreamCycle = dreamCycle;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _mesh.RegisterAsync(_input, cancellationToken);
        await _mesh.RegisterAsync(_context, cancellationToken);
        await _mesh.RegisterAsync(_routing, cancellationToken);
        await _mesh.RegisterAsync(_capability, cancellationToken);
        await _mesh.RegisterAsync(_storage, cancellationToken);
        await _mesh.RegisterAsync(_output, cancellationToken);
        await _mesh.RegisterAsync(_communication, cancellationToken);
        await _mesh.RegisterAsync(_task, cancellationToken);
        await _mesh.RegisterAsync(_self, cancellationToken);
        await _mesh.RegisterAsync(_evolution, cancellationToken);

        _guardian.StartMonitoring(TimeSpan.FromSeconds(15));
        _logger.LogInformation("LivingTreeSystem initialized with 10 governors, DNA: {DNA}",
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

            var response = await ProcessAsync(query, cancellationToken);
            _journal.Complete(entry, response[..Math.Min(response.Length, 500)]);

            _ = Task.Run(async () =>
            {
                try { await SilentSelfCheckAsync(response); }
                catch (Exception ex) { _logger.LogDebug(ex, "Silent self-check failed"); }
            }, cancellationToken).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                    _logger.LogDebug(t.Exception, "Silent self-check background task failed");
            }, TaskContinuationOptions.OnlyOnFaulted);

            if (_dna != null && !string.IsNullOrEmpty(response))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _dna.ProcessAsync(query, response, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "DNA background process failed");
                    }
                }, cancellationToken).ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                        _logger.LogDebug(t.Exception, "DNA background task failed");
                }, TaskContinuationOptions.OnlyOnFaulted);
            }

            return response;
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
        var model = "";
        List<ChatMessage> messages;

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

        try
        {
            var routingResult = await _routing.ProcessAsync(new Handshake
            {
                To = "routing", Action = "select_provider",
                Payload = new Dictionary<string, object?> { ["query"] = query }
            }, cancellationToken);

            model = routingResult.Payload?.GetValueOrDefault("model")?.ToString() ?? DefaultModel;
        }
        catch
        {
            model = DefaultModel;
        }

        var streamOptions = new ChatOptions { ModelId = model, Temperature = 0.3f, MaxOutputTokens = 4096, Tools = _toolRegistry.GetTools().ToList() };
        messages = new List<ChatMessage> { new(ChatRole.User, query) }; 

        IAsyncEnumerable<ChatResponseUpdate> streamResponse;
        try { streamResponse = _llm.GetStreamingResponseAsync(messages, streamOptions, cancellationToken); }
        catch (Exception ex) { streamResponse = null!; _logger.LogError(ex, "Stream init failed"); }

        if (streamResponse == null)
        {
            yield return "Error connecting to provider.";
            yield break;
        }

        await foreach (var update in streamResponse)
        {
            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
        }
    }

    public async IAsyncEnumerable<string> StreamWithModelAsync(
        string query,
        string modelId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var modelOptions = new ChatOptions { ModelId = modelId, Temperature = 0.3f, MaxOutputTokens = 4096, Tools = _toolRegistry.GetTools().ToList() };
        var modelMessages = new List<ChatMessage> { new(ChatRole.User, query) };
        IAsyncEnumerable<ChatResponseUpdate> modelStream;

        try { modelStream = _llm.GetStreamingResponseAsync(modelMessages, modelOptions, cancellationToken); }
        catch { modelStream = null!; }

        if (modelStream == null)
        {
            yield return $"Error: model {modelId} unavailable";
            yield break;
        }

        await foreach (var update in modelStream)
        {
            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
        }
    }

    public async Task<string> ProcessAsync(string query, CancellationToken cancellationToken = default)
    {
        var result = await ProcessTypedAsync(GovernorInput.Create(query), cancellationToken);
        return result.Response;
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
            try
            {
                await _dna.Consciousness.ProcessExperienceAsync(query, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DNA consciousness processing skipped");
            }
        }

        var inputResult = await _input.ProcessAsync(new Handshake
        {
            To = "input", Action = "process",
            Payload = new Dictionary<string, object?> { ["query"] = query },
            ReplyTo = traceId
        }, cancellationToken);

        if (inputResult.Action == "reflex")
            return GovernorOutput.Reflex(
                inputResult.Payload?.GetValueOrDefault("command")?.ToString() ?? "",
                traceId);

        var label = inputResult.Payload?.GetValueOrDefault("label")?.ToString() ?? "deep";
        var complexity = inputResult.Payload?.GetValueOrDefault("complexity") is float c ? c : 0.5f;
        var emotion = inputResult.Payload?.GetValueOrDefault("emotion")?.ToString();

        if (_duplexRouter != null)
        {
            var routeResult = _duplexRouter.Route(query);
            if (routeResult.CanAnswerLocally)
            {
                _logger.LogInformation("L1 answered locally: route={Route}, confidence={Conf:F2}",
                    routeResult.Route, routeResult.Confidence);
                return GovernorOutput.Success(routeResult.LocalResponse, traceId);
            }

            if (routeResult.Route == "delegate_l2")
            {
                var duplexContextResult = await _context.ProcessAsync(new Handshake
                {
                    To = "context", Action = "preload",
                    Payload = inputResult.Payload, ReplyTo = traceId
                }, cancellationToken);

                var duplexContext = duplexContextResult.Payload?.GetValueOrDefault("context")?.ToString() ?? "";
                var fullQuery = string.IsNullOrEmpty(duplexContext) ? query : $"Context:\n{duplexContext}\n\nQuery: {query}";

                var teachingResult = await _duplexRouter.RequestL2ReasoningAsync(fullQuery, routeResult, cancellationToken);
                if (teachingResult != null)
                {
                    _duplexRouter.LearnFromL2(query, teachingResult);
                    _logger.LogInformation("L2 teaching received, L1 learned from teacher");

                    var duplexOutputResult = await _output.ProcessAsync(new Handshake
                    {
                        To = "output", Action = "review",
                        Payload = new Dictionary<string, object?> { ["response"] = teachingResult.Answer },
                        ReplyTo = traceId
                    }, cancellationToken);

                    _context.AddTurn(query, teachingResult.Answer);
                    var finalResponse = duplexOutputResult.Payload?.GetValueOrDefault("response")?.ToString() ?? teachingResult.Answer;

                    _duplexRouter.CacheResponse(query, finalResponse, "delegate_l2", "general", routeResult.Confidence);

                    return GovernorOutput.Success(finalResponse, traceId);
                }
            }
        }

        var contextResult = await _context.ProcessAsync(new Handshake
        {
            To = "context", Action = "preload",
            Payload = inputResult.Payload, ReplyTo = traceId
        }, cancellationToken);

        var routingResult = await _routing.ProcessAsync(new Handshake
        {
            To = "routing", Action = "select_provider",
            Payload = inputResult.Payload, ReplyTo = traceId
        }, cancellationToken);

        var model = routingResult.Payload?.GetValueOrDefault("model")?.ToString() ?? DefaultModel;
        var temperature = routingResult.Payload?.GetValueOrDefault("temperature") is float t ? t : 0.3f;
        var context = contextResult.Payload?.GetValueOrDefault("context")?.ToString() ?? "";
        var fullPrompt = string.IsNullOrEmpty(context) ? query : $"Context:\n{context}\n\nQuery: {query}";

        string response;
        var options = new ChatOptions { ModelId = model, Temperature = temperature, MaxOutputTokens = 4096, Tools = _toolRegistry.GetTools().ToList() };

        try
        {
            if (label == "fast" || label == "reflex")
            {
                var fastResponse = await _llm.GetResponseAsync(fullPrompt, options, cancellationToken);
                response = fastResponse.Text ?? "";
            }
            else if (_fanOut != null)
            {
                var fanOutResult = await _fanOut.RaceAsync(fullPrompt, maxConcurrent: 3, cancellationToken: cancellationToken);
                response = fanOutResult.Answer;
                _logger.LogDebug("Fan-out race winner: {Provider} ({Latency}ms)", fanOutResult.WinningProvider, fanOutResult.LatencyMs);
            }
            else
            {
                response = await CollaborativeChatAsync(fullPrompt, options, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var fallbackModel = GetDegradedModel(model);
            if (fallbackModel != model)
            {
                _logger.LogWarning("Model {Model} failed, falling back to {Fallback}: {Error}", model, fallbackModel, ex.Message);
                options.ModelId = fallbackModel;
                options.Temperature = 0.3f;
                var fallbackResponse = await _llm.GetResponseAsync(fullPrompt, options, cancellationToken);
                response = fallbackResponse.Text ?? "";
            }
            else { throw; }
        }

        if (_dna != null)
        {
            try
            {
                var outputSafety = await _dna.Safety.EvaluateOutputAsync(response, cancellationToken);
                if (!outputSafety.Allowed)
                {
                    _logger.LogWarning("DNA safety blocked output: {Reason}", outputSafety.BlockReason);
                    return GovernorOutput.Blocked(outputSafety.BlockReason);
                }
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

        _duplexRouter?.CacheResponse(query, response, label, "general", complexity);

        if (_reasoning != null && !string.IsNullOrEmpty(response))
        {
            try { response = await _reasoning.EnhanceResponse(query, response); }
            catch (Exception ex) { _logger.LogDebug(ex, "Reasoning enhancement skipped"); }
        }

        _synapticMemory?.Store(new SynapticExperience
        {
            Type = SynapseType.Interaction,
            Query = query,
            Response = response,
            Label = label,
            Confidence = complexity,
            Reward = 0.7f,
            Metadata = $"model={model},trace={traceId}"
        });

        _dreamCycle?.RecordInteraction();

        _ = Task.Run(async () =>
        {
            try
            {
                await _self.ProcessAsync(new Handshake
                {
                    To = "self", Action = "start_trace",
                    Payload = new Dictionary<string, object?> { ["trace_id"] = traceId }
                }, cancellationToken);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Self governor trace failed"); }
        }, cancellationToken).ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
                _logger.LogDebug(t.Exception, "Self governor background task failed");
        }, TaskContinuationOptions.OnlyOnFaulted);

        return GovernorOutput.Success(response, traceId);
    }

    private async Task<string> CollaborativeChatAsync(string prompt, ChatOptions baseOptions, CancellationToken cancellationToken)
    {
        var history = _context.CompressHistory();

        var iterativePrompt = string.IsNullOrEmpty(history)
            ? prompt
            : $"Previous conversation:\n{history}\n\nCurrent query:\n{prompt}\n\nPlease provide a thorough, well-reasoned response.";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));

        var response = await _llm.CompleteAsync(iterativePrompt, baseOptions, cts.Token);

        var reviewPrompt = $"Review this response for accuracy and completeness. If it needs improvement, provide the improved version:\n\n{response}";
        var reviewOptions = new ChatOptions { ModelId = baseOptions.ModelId, Temperature = 0.1f, MaxOutputTokens = 8192 };

        using var reviewCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        reviewCts.CancelAfter(TimeSpan.FromSeconds(30));

        var reviewed = await _llm.CompleteAsync(reviewPrompt, reviewOptions, reviewCts.Token);

        return reviewed;
    }

    private string HandleReflex(Handshake inputResult)
    {
        var command = inputResult.Payload?.GetValueOrDefault("command")?.ToString() ?? "";
        return command switch
        {
            "/help" => "LivingTree AI Agent v5.5 (.NET 10)\n" +
                       "Commands: /help /status /pause /resume /restart\n" +
                       $"DNA: {(_dna != null ? $"active (L{_dna.GetStatus().ConsciousnessLevel})" : "disabled")}",
            "/status" => $"Mode: {_guardian.Mode}\n" +
                         $"Journal: {_journal.Entries.Count} entries\n" +
                          $"DNA: {(_dna != null ? $"consciousness={_dna.Consciousness.State.Level}, awareness={_dna.Consciousness.State.AwarenessScore:F2}" : "disabled")}\n" +
                         $"Biorhythm: {(_dna != null ? $"{_dna.Life.Biorhythm.Phase}, energy={_dna.Life.Biorhythm.EnergyLevel:F2}" : "disabled")}",
            "/pause" => "Journal paused.",
            "/resume" => "Journal resumed.",
            "/restart" => RestartSystem(),
            _ => $"Unknown command: {command}"
        };
    }

    private string RestartSystem()
    {
        _guardian.ResetErrors();
        _journal.Clear();
        _logger.LogInformation("System restarted: journal cleared, guardian reset");
        return "System restarted. Journal cleared, error counters reset.";
    }

    private async Task SilentSelfCheckAsync(string response)
    {
        try
        {
            var result = await _output.SilentSelfCheckAsync(response);
            _logger.LogInformation("Silent self-check: {Result}", result);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Silent self-check skipped");
        }
    }
}
