using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LTAI.MAF;

public enum A2ATaskState { Pending, Working, InputRequired, Completed, Failed, Canceled }

public enum A2APartType { Text, File, Data }

public sealed class AgentCard
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "1.0";
    [JsonPropertyName("url")] public string Url { get; init; } = "";
    [JsonPropertyName("capabilities")] public AgentCapabilities Capabilities { get; init; } = new();
    [JsonPropertyName("skills")] public List<AgentSkill> Skills { get; init; } = new();
    [JsonPropertyName("defaultInputModes")] public List<string> DefaultInputModes { get; init; } = new() { "text" };
    [JsonPropertyName("defaultOutputModes")] public List<string> DefaultOutputModes { get; init; } = new() { "text" };
    [JsonPropertyName("authSchemes")] public List<string>? AuthSchemes { get; init; }
}

public sealed class AgentCapabilities
{
    [JsonPropertyName("streaming")] public bool Streaming { get; init; }
    [JsonPropertyName("pushNotifications")] public bool PushNotifications { get; init; }
    [JsonPropertyName("stateTransitionHistory")] public bool StateTransitionHistory { get; init; }
}

public sealed class AgentSkill
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; init; } = new();
    [JsonPropertyName("examples")] public List<string>? Examples { get; init; }
    [JsonPropertyName("inputModes")] public List<string>? InputModes { get; init; }
    [JsonPropertyName("outputModes")] public List<string>? OutputModes { get; init; }
}

public sealed class A2APart
{
    [JsonPropertyName("type")] public string Type { get; init; } = "text";

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    [JsonPropertyName("file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public A2AFilePart? File { get; init; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; init; }

    public static A2APart FromText(string text) => new() { Type = "text", Text = text };
    public static A2APart FromFile(string name, string mimeType, string? base64Content = null, string? uri = null)
        => new() { Type = "file", File = new A2AFilePart { Name = name, MimeType = mimeType, Base64Content = base64Content, Uri = uri } };
    public static A2APart FromData(object data)
        => new() { Type = "data", Data = JsonSerializer.SerializeToElement(data) };
}

public sealed class A2AFilePart
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("mimeType")] public string MimeType { get; init; } = "";
    [JsonPropertyName("base64Content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Base64Content { get; init; }
    [JsonPropertyName("uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Uri { get; init; }
}

public sealed class A2ATask
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("sessionId")] public string SessionId { get; init; } = "";
    [JsonPropertyName("state")] public A2ATaskState State { get; set; } = A2ATaskState.Pending;
    [JsonPropertyName("messages")] public List<A2AMessage> Messages { get; init; } = new();
    [JsonPropertyName("artifacts")] public List<A2AArtifact> Artifacts { get; init; } = new();
    [JsonPropertyName("statusMessage")] public string? StatusMessage { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public void AddMessage(string role, List<A2APart> parts)
    {
        Messages.Add(new A2AMessage { Role = role, Parts = parts });
        UpdatedAt = DateTime.UtcNow;
    }

    public void AddArtifact(string name, string url, string mimeType, string? description = null)
    {
        Artifacts.Add(new A2AArtifact { Name = name, Url = url, MimeType = mimeType, Description = description });
        UpdatedAt = DateTime.UtcNow;
    }
}

public sealed class A2AMessage
{
    [JsonPropertyName("role")] public string Role { get; init; } = "user";
    [JsonPropertyName("parts")] public List<A2APart> Parts { get; init; } = new();
}

public sealed class A2AArtifact
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("url")] public string Url { get; init; } = "";
    [JsonPropertyName("mimeType")] public string MimeType { get; init; } = "";
    [JsonPropertyName("description")] public string? Description { get; init; }
}

public sealed class A2AHost
{
    private readonly LTAIAgent _agent;
    private readonly ILogger<A2AHost> _logger;
    private readonly ConcurrentDictionary<string, A2ASession> _sessions = new();
    private readonly ConcurrentDictionary<string, A2ATask> _tasks = new();

    public A2AHost(LTAIAgent agent, ILogger<A2AHost> logger)
    {
        _agent = agent;
        _logger = logger;
    }

    public AgentCard GetAgentCard()
    {
        return new AgentCard
        {
            Name = _agent.Name,
            Description = _agent.Description ?? "LTAI Multi-Agent Framework agent",
            Version = "5.5.0",
            Url = $"a2a://{_agent.Name}",
            Capabilities = new AgentCapabilities
            {
                Streaming = true,
                PushNotifications = false,
                StateTransitionHistory = true
            },
            Skills = new List<AgentSkill>
            {
                new() { Id = "chat", Name = "Chat", Description = "General conversation", Tags = new() { "text", "conversation" } },
                new() { Id = "reason", Name = "Reason", Description = "Complex reasoning", Tags = new() { "reasoning", "analysis" } },
                new() { Id = "code", Name = "Code", Description = "Code analysis", Tags = new() { "code", "development" } }
            },
            DefaultInputModes = new() { "text" },
            DefaultOutputModes = new() { "text" }
        };
    }

    public A2ATask CreateTask(string? sessionId = null)
    {
        sessionId ??= Guid.NewGuid().ToString("N");
        var task = new A2ATask
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            SessionId = sessionId,
            State = A2ATaskState.Pending
        };
        _tasks[task.Id] = task;
        return task;
    }

    public A2ATask? GetTask(string taskId) =>
        _tasks.TryGetValue(taskId, out var task) ? task : null;

    public bool CancelTask(string taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.State = A2ATaskState.Canceled;
            task.StatusMessage = "Canceled by request";
            task.UpdatedAt = DateTime.UtcNow;
            return true;
        }
        return false;
    }

    public async Task<A2AResponse> ProcessAgentMessageAsync(
        A2ARequest request,
        CancellationToken cancellationToken = default)
    {
        var session = _sessions.GetOrAdd(request.SessionId ?? Guid.NewGuid().ToString("N"),
            _ => new A2ASession());

        session.LastActivity = DateTime.UtcNow;

        _logger.LogInformation("A2A request: {Action} from {From} session {Session}",
            request.Action, request.FromAgent, request.SessionId);

        if (request.Action == "tasks/send" && request.TaskId != null)
        {
            return await HandleTaskSendAsync(request, session, cancellationToken);
        }

        if (request.Action == "tasks/get" && request.TaskId != null)
        {
            return HandleTaskGet(request);
        }

        if (request.Action == "tasks/cancel" && request.TaskId != null)
        {
            return HandleTaskCancel(request);
        }

        var parts = request.Parts?.Count > 0 ? request.Parts
            : new List<A2APart> { A2APart.FromText(request.Content ?? "") };

        var textContent = string.Join("\n", parts
            .Where(p => p.Type == "text")
            .Select(p => p.Text ?? ""));
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(request.Role == "system"
                ? Microsoft.Extensions.AI.ChatRole.System
                : Microsoft.Extensions.AI.ChatRole.User, textContent)
        };

        var response = await _agent.GetResponseAsync(messages, null, cancellationToken);

        return new A2AResponse
        {
            SessionId = session.SessionId,
            Content = response.Text ?? "",
            FromAgent = _agent.Name,
            Action = "response",
            Parts = new List<A2APart> { A2APart.FromText(response.Text ?? "") },
            Metadata = new Dictionary<string, string>
            {
                ["agent"] = _agent.Name,
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            }
        };
    }

    private async Task<A2AResponse> HandleTaskSendAsync(
        A2ARequest request, A2ASession session, CancellationToken ct)
    {
        var task = _tasks.GetOrAdd(request.TaskId!, _ => new A2ATask
        {
            Id = request.TaskId!,
            SessionId = request.SessionId ?? ""
        });

        task.State = A2ATaskState.Working;
        task.UpdatedAt = DateTime.UtcNow;

        if (request.Parts != null)
            task.AddMessage(request.Role, request.Parts);
        else if (!string.IsNullOrWhiteSpace(request.Content))
            task.AddMessage(request.Role, new List<A2APart> { A2APart.FromText(request.Content) });

        var textContent = string.Join("\n", (request.Parts ?? new List<A2APart>())
            .Where(p => p.Type == "text")
            .Select(p => p.Text ?? ""));
        if (string.IsNullOrWhiteSpace(textContent))
            textContent = request.Content ?? "";

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.User, textContent)
        };

        var response = await _agent.GetResponseAsync(messages, null, ct);

        task.State = A2ATaskState.Completed;
        task.StatusMessage = "Task completed";
        task.AddMessage("agent", new List<A2APart> { A2APart.FromText(response.Text ?? "") });
        task.UpdatedAt = DateTime.UtcNow;

        return new A2AResponse
        {
            SessionId = session.SessionId,
            TaskId = task.Id,
            TaskState = task.State,
            Content = response.Text ?? "",
            FromAgent = _agent.Name,
            Action = "tasks/send",
            Parts = new List<A2APart> { A2APart.FromText(response.Text ?? "") }
        };
    }

    private A2AResponse HandleTaskGet(A2ARequest request)
    {
        var task = _tasks.TryGetValue(request.TaskId!, out var t) ? t : null;
        return new A2AResponse
        {
            SessionId = request.SessionId ?? "",
            TaskId = request.TaskId,
            TaskState = task?.State,
            TaskStatusMessage = task?.StatusMessage,
            FromAgent = _agent.Name,
            Action = "tasks/get",
            Content = task != null ? JsonSerializer.Serialize(new { task.Id, task.State, task.StatusMessage }) : ""
        };
    }

    private A2AResponse HandleTaskCancel(A2ARequest request)
    {
        var canceled = CancelTask(request.TaskId!);
        return new A2AResponse
        {
            SessionId = request.SessionId ?? "",
            TaskId = request.TaskId,
            TaskState = A2ATaskState.Canceled,
            FromAgent = _agent.Name,
            Action = "tasks/cancel",
            Content = canceled ? "Task canceled" : "Task not found"
        };
    }

    public IReadOnlyList<A2ASession> GetActiveSessions()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
        return _sessions.Values
            .Where(s => s.LastActivity > cutoff)
            .ToList()
            .AsReadOnly();
    }

    public IReadOnlyList<A2ATask> GetTasksByState(A2ATaskState state)
    {
        return _tasks.Values.Where(t => t.State == state).ToList().AsReadOnly();
    }

    public IReadOnlyList<A2ATask> GetAllTasks()
    {
        return _tasks.Values.ToList().AsReadOnly();
    }

    public void CleanupExpiredSessions(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var expired = _sessions.Keys
            .Where(k => _sessions.TryGetValue(k, out var s) && s.LastActivity < cutoff)
            .ToList();

        foreach (var key in expired)
            _sessions.TryRemove(key, out _);

        if (expired.Count > 0)
            _logger.LogInformation("A2A purged {Count} expired sessions", expired.Count);
    }
}

public sealed class A2ARequest
{
    public string? SessionId { get; init; }
    public string? TaskId { get; init; }
    public string FromAgent { get; init; } = "";
    public string Action { get; init; } = "chat";
    public string? Content { get; init; }
    public List<A2APart>? Parts { get; init; }
    public string Role { get; init; } = "user";
    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed class A2AResponse
{
    public string SessionId { get; init; } = "";
    public string? TaskId { get; init; }
    public A2ATaskState? TaskState { get; init; }
    public string? TaskStatusMessage { get; init; }
    public string Content { get; init; } = "";
    public List<A2APart>? Parts { get; init; }
    public string FromAgent { get; init; } = "";
    public string Action { get; init; } = "response";
    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed class A2ASession
{
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
}
