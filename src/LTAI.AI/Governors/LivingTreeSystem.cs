using LTAI.Core.Execution;
using LTAI.Core.Interfaces;
using LTAI.Core.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed class LivingTreeSystem
{
    private readonly ICognitiveMesh _mesh;
    private readonly TaskJournal _journal;
    private readonly IProviderEngine _llm;
    private readonly ILogger<LivingTreeSystem> _logger;

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

    public SystemGuardian Guardian => _guardian;
    public SystemMode Mode => _guardian.Mode;

    public LivingTreeSystem(
        ICognitiveMesh mesh,
        TaskJournal journal,
        IProviderEngine llm,
        ILogger<LivingTreeSystem> logger,
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
        SystemGuardian guardian)
    {
        _mesh = mesh;
        _journal = journal;
        _llm = llm;
        _logger = logger;
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
        _logger.LogInformation("LivingTreeSystem initialized with 10 governors");
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

            var response = await ProcessAsync(query, cancellationToken);
            _journal.Complete(entry, response[..Math.Min(response.Length, 500)]);

            _ = SilentSelfCheckAsync(response);

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

    public async Task<string> ProcessAsync(string query, CancellationToken cancellationToken = default)
    {
        var traceId = Guid.NewGuid().ToString("N");

        if (_journal.TryConsumeMessage(out var humanMessage) && humanMessage != null)
        {
            _logger.LogInformation("Human message injected: {Message}", humanMessage[..Math.Min(humanMessage.Length, 100)]);
            query = humanMessage;
        }

        var inputResult = await _input.ProcessAsync(new Handshake
        {
            To = "input",
            Action = "process",
            Payload = new Dictionary<string, object?> { ["query"] = query },
            ReplyTo = traceId
        }, cancellationToken);

        if (inputResult.Action == "reflex")
        {
            return HandleReflex(inputResult);
        }

        var contextResult = await _context.ProcessAsync(new Handshake
        {
            To = "context",
            Action = "preload",
            Payload = inputResult.Payload,
            ReplyTo = traceId
        }, cancellationToken);

        var routingResult = await _routing.ProcessAsync(new Handshake
        {
            To = "routing",
            Action = "select_provider",
            Payload = inputResult.Payload,
            ReplyTo = traceId
        }, cancellationToken);

        var model = routingResult.Payload?.GetValueOrDefault("model")?.ToString() ?? "deepseek-v4-pro";
        var temperature = routingResult.Payload?.GetValueOrDefault("temperature") is float t ? t : 0.3f;
        var label = inputResult.Payload?.GetValueOrDefault("label")?.ToString() ?? "deep";

        var context = contextResult.Payload?.GetValueOrDefault("context")?.ToString() ?? "";
        var fullPrompt = string.IsNullOrEmpty(context) ? query : $"Context:\n{context}\n\nQuery: {query}";

        string response;

        if (label == "fast" || label == "reflex")
        {
            response = await _llm.ChatAsync(fullPrompt, new LLMChatOptions
            {
                Model = model,
                Temperature = temperature,
                MaxTokens = 4096
            }, cancellationToken);
        }
        else
        {
            response = await CollaborativeChatAsync(fullPrompt, model, temperature, cancellationToken);
        }

        var outputResult = await _output.ProcessAsync(new Handshake
        {
            To = "output",
            Action = "review",
            Payload = new Dictionary<string, object?> { ["response"] = response },
            ReplyTo = traceId
        }, cancellationToken);

        _context.AddTurn(query, response);

        response = outputResult.Payload?.GetValueOrDefault("response")?.ToString() ?? response;

        _ = _self.ProcessAsync(new Handshake
        {
            To = "self",
            Action = "start_trace",
            Payload = new Dictionary<string, object?> { ["trace_id"] = traceId }
        }, CancellationToken.None);

        return response;
    }

    private async Task<string> CollaborativeChatAsync(string prompt, string model, float temperature, CancellationToken cancellationToken)
    {
        var history = _context.CompressHistory();

        var iterativePrompt = string.IsNullOrEmpty(history)
            ? prompt
            : $"Previous conversation:\n{history}\n\nCurrent query:\n{prompt}\n\nPlease provide a thorough, well-reasoned response.";

        var response = await _llm.ChatAsync(iterativePrompt, new LLMChatOptions
        {
            Model = model,
            Temperature = temperature,
            MaxTokens = 8192
        }, cancellationToken);

        var reviewPrompt = $"Review this response for accuracy and completeness. If it needs improvement, provide the improved version:\n\n{response}";
        var reviewed = await _llm.ChatAsync(reviewPrompt, new LLMChatOptions
        {
            Model = model,
            Temperature = 0.1f,
            MaxTokens = 8192
        }, cancellationToken);

        return reviewed;
    }

    private string HandleReflex(Handshake inputResult)
    {
        var command = inputResult.Payload?.GetValueOrDefault("command")?.ToString() ?? "";
        return command switch
        {
            "/help" => "LivingTree AI Agent v5.5 (.NET 10)\nCommands: /help /status /pause /resume /restart",
            "/status" => $"Mode: {_guardian.Mode}, Journal entries: {_journal.Entries.Count}",
            "/pause" => "Journal paused.",
            "/resume" => "Journal resumed.",
            "/restart" => "Restart not implemented yet.",
            _ => $"Unknown command: {command}"
        };
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
