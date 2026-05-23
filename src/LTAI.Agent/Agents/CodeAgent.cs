using System.Runtime.CompilerServices;
using System.Text.Json;
using LTAI.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Agents;

public sealed class CodeAgent : AIAgent
{
    private readonly ChatClientAgent _inner;
    private readonly ILogger<CodeAgent> _logger;
    private readonly HashSet<string> _supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".ts", ".go", ".rs", ".java", ".kt", ".swift", ".cpp", ".c", ".h",
        ".json", ".yaml", ".yml", ".xml", ".md", ".sql", ".sh", ".ps1", ".toml", ".csproj"
    };

    public override string Name { get; }
    public override string Description { get; }

    public CodeAgent(
        IChatClient chatClient,
        LTAIAgentCard card,
        IEnumerable<Microsoft.Extensions.AI.AITool> tools,
        ILogger<CodeAgent> logger)
    {
        Name = card.Name;
        Description = card.Instructions;
        _logger = logger;

        _inner = chatClient.AsBuilder().BuildAIAgent(new ChatClientAgentOptions
        {
            Name = card.Name,
            Description = card.Instructions,
            ChatOptions = new() { Tools = tools.ToList() }
        });
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);
        if (userMsg is null)
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, "No code analysis request received."));

        var query = userMsg.Text ?? "";
        _logger.LogInformation("CodeAgent [{Name}]: {Query}", Name, query[..Math.Min(query.Length, 200)]);

        var filePaths = ExtractFilePaths(query);
        var contextMessages = new List<ChatMessage>();

        if (filePaths.Count > 0)
        {
            foreach (var fp in filePaths.Take(5))
            {
                var ext = Path.GetExtension(fp);
                if (!_supportedExtensions.Contains(ext))
                {
                    _logger.LogWarning("CodeAgent: Unsupported file extension {Ext} for {Path}", ext, fp);
                    continue;
                }

                if (!ValidateFilePath(fp))
                {
                    _logger.LogWarning("CodeAgent: Path traversal attempt blocked: {Path}", fp);
                    contextMessages.Add(new ChatMessage(ChatRole.System,
                        $"File {fp}: access denied - path traversal not allowed"));
                    continue;
                }

                if (!File.Exists(fp))
                {
                    _logger.LogWarning("CodeAgent: File not found: {Path}", fp);
                    continue;
                }

                try
                {
                    var content = await File.ReadAllTextAsync(fp, cancellationToken);
                    var truncated = content.Length > 10000 ? content[..10000] + $"\n... ({content.Length} total chars)" : content;
                    contextMessages.Add(new ChatMessage(ChatRole.System,
                        $"File: {fp} ({content.Length} chars)\n```{ext.TrimStart('.')}\n{truncated}\n```"));
                    _logger.LogDebug("CodeAgent: Preloaded {Path} ({Length} chars)", fp, content.Length);
                }
                catch (Exception ex)
                {
                    contextMessages.Add(new ChatMessage(ChatRole.System, $"File {fp}: read error - {ex.Message}"));
                }
            }
        }

        if (contextMessages.Count > 0)
            msgList.InsertRange(0, contextMessages);

        var enhancedQuery = $"""
            Code analysis task. Be precise, cite line numbers, and flag:
            - Security issues (SQL injection, XSS, hardcoded secrets, unsafe deserialization)
            - Performance problems (N+1 queries, excessive allocations, blocking calls)
            - Design problems (violations of SOLID, tight coupling, missing abstractions)

            Request: {query}
            """;

        var enhancedMessages = new List<ChatMessage>(msgList.Take(msgList.Count - 1))
        {
            new(ChatRole.User, enhancedQuery)
        };

        _logger.LogInformation("CodeAgent [{Name}]: Analyzing {FileCount} files", Name, filePaths.Count);

        var response = await _inner.RunAsync(enhancedMessages, session, options, cancellationToken);

        const int MaxCorrectionAttempts = 2;
        for (int attempt = 0; attempt < MaxCorrectionAttempts; attempt++)
        {
            var validationFeedback = ValidateCodeResponse(response.Text ?? "");
            if (string.IsNullOrWhiteSpace(validationFeedback))
                break;

            _logger.LogWarning("CodeAgent [{Name}]: Correction attempt {Attempt}/{Max}", Name, attempt + 1, MaxCorrectionAttempts);

            var fixedMessages = new List<ChatMessage>(enhancedMessages)
            {
                new(ChatRole.Assistant, response.Text ?? ""),
                new(ChatRole.User, $"Review feedback: {validationFeedback}\nPlease address these issues.")
            };
            response = await _inner.RunAsync(fixedMessages, session, options, cancellationToken);
        }

        _logger.LogInformation("CodeAgent [{Name}]: Analysis complete", Name);
        return response;
    }

    private static List<string> ExtractFilePaths(string text)
    {
        var paths = new List<string>();
        foreach (var word in text.Split(' ', '\n', '\t', '"', '\''))
        {
            var w = word.Trim();
            if (w.Contains('.') && (w.Contains('/') || w.Contains('\\') || w.EndsWith(".cs") || w.EndsWith(".py") || w.EndsWith(".js")))
                paths.Add(w);
        }
        return paths;
    }

    private static bool ValidateFilePath(string path)
    {
        try
        {
            // Normalize path to absolute
            var fullPath = Path.GetFullPath(path);
            var currentDir = Environment.CurrentDirectory;

            // Check for directory traversal patterns
            if (path.Contains("../") || path.Contains("..\\") || path.Contains(".."))
            {
                return false;
            }

            // Ensure path is within current directory or its subdirectories
            if (!fullPath.StartsWith(currentDir, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Block access to sensitive system directories
            var sensitivePaths = new[]
            {
                "/etc/", "/proc/", "/sys/", "/dev/",
                "C:\\Windows\\", "C:\\Program Files\\", "C:\\Program Files (x86)\\"
            };

            foreach (var sensitive in sensitivePaths)
            {
                if (fullPath.StartsWith(sensitive, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
        catch
        {
            // If path normalization fails, reject it
            return false;
        }
    }

    private static string ValidateCodeResponse(string response)
    {
        var warnings = new List<string>();

        if (response.Contains("delete all", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("drop table", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("rm -rf /", StringComparison.OrdinalIgnoreCase))
            warnings.Add("Response contains potentially destructive commands — verify before execution.");

        if (response.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("secret", StringComparison.OrdinalIgnoreCase))
            warnings.Add("Response may contain sensitive information — sanitize before sharing.");

        return string.Join("\n", warnings);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in _inner.RunStreamingAsync(messages, session, options, cancellationToken))
            yield return update;
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => _inner.CreateSessionAsync(cancellationToken);

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? o = null, CancellationToken ct = default)
        => _inner.SerializeSessionAsync(session, o, ct);

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement state, JsonSerializerOptions? o = null, CancellationToken ct = default)
        => _inner.DeserializeSessionAsync(state, o, ct);
}
