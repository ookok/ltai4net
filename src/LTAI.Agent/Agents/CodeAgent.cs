using System.Runtime.CompilerServices;
using System.Text.Json;
using LTAI.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Agents;

public sealed class CodeAgent : BaseAgent
{
    private static readonly HashSet<string> _supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".ts", ".go", ".rs", ".java", ".kt", ".swift", ".cpp", ".c", ".h",
        ".json", ".yaml", ".yml", ".xml", ".md", ".sql", ".sh", ".ps1", ".toml", ".csproj"
    };

    public CodeAgent(
        LTAIAgentCard card,
        IChatClient brain,
        SkillRegistry skills,
        ILogger<CodeAgent> logger)
        : base(card, brain, skills, logger)
    {
        RegisterStrategy(new DefaultCodeAnalysisStrategy(brain, logger, _supportedExtensions));
    }

    protected override async Task<AgentResponse> ExecuteLogicAsync(
        AgentContext context, CancellationToken ct)
    {
        var query = context.UserQuery;
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
                    var content = await File.ReadAllTextAsync(fp, ct).ConfigureAwait(false);
                    var truncated = content.Length > 10000
                        ? content[..10000] + $"\n... ({content.Length} total chars)"
                        : content;
                    contextMessages.Add(new ChatMessage(ChatRole.System,
                        $"File: {fp} ({content.Length} chars)\n```{ext.TrimStart('.')}\n{truncated}\n```"));
                    _logger.LogDebug("CodeAgent: Preloaded {Path} ({Length} chars)", fp, content.Length);
                }
                catch (Exception ex)
                {
                    contextMessages.Add(new ChatMessage(ChatRole.System,
                        $"File {fp}: read error - {ex.Message}"));
                }
            }
        }

        if (contextMessages.Count > 0)
            context.FullHistory.InsertRange(0, contextMessages);

        var enhancedQuery = $"""
            Code analysis task. Be precise, cite line numbers, and flag:
            - Security issues (SQL injection, XSS, hardcoded secrets, unsafe deserialization)
            - Performance problems (N+1 queries, excessive allocations, blocking calls)
            - Design problems (violations of SOLID, tight coupling, missing abstractions)

            Request: {query}
            """;

        var enhancedMessages = new List<ChatMessage>(context.FullHistory.Take(context.FullHistory.Count - 1))
        {
            new(ChatRole.User, enhancedQuery)
        };

        _logger.LogInformation("CodeAgent [{Name}]: Analyzing {FileCount} files", Name, filePaths.Count);

        return await CallBrainWithCorrectionAsync(enhancedMessages, ct).ConfigureAwait(false);
    }

    private async Task<AgentResponse> CallBrainWithCorrectionAsync(
        List<ChatMessage> messages, CancellationToken ct, int maxAttempts = 2)
    {
        var response = await CallBrainAsync(messages, ct: ct).ConfigureAwait(false);
        var text = response.Text ?? "";

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var validationFeedback = ValidateCodeResponse(text);
            if (string.IsNullOrWhiteSpace(validationFeedback))
                break;

            _logger.LogWarning("CodeAgent: Correction attempt {Attempt}/{Max}", attempt + 1, maxAttempts);

            messages.Add(new(ChatRole.Assistant, text));
            messages.Add(new(ChatRole.User, $"Review feedback: {validationFeedback}\nPlease address these issues."));
            response = await CallBrainAsync(messages, ct: ct).ConfigureAwait(false);
            text = response.Text ?? "";
        }

        return response;
    }

    private static List<string> ExtractFilePaths(string text)
    {
        var paths = new List<string>();
        foreach (var word in text.Split(' ', '\n', '\t', '"', '\''))
        {
            var w = word.Trim();
            if (w.Contains('.') && (w.Contains('/') || w.Contains('\\') ||
                w.EndsWith(".cs") || w.EndsWith(".py") || w.EndsWith(".js")))
                paths.Add(w);
        }
        return paths;
    }

    private static bool ValidateFilePath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var currentDir = Environment.CurrentDirectory;

            if (path.Contains("../") || path.Contains("..\\") || path.Contains(".."))
                return false;

            if (!fullPath.StartsWith(currentDir, StringComparison.OrdinalIgnoreCase))
                return false;

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
        await foreach (var update in CallBrainStreamingAsync(messages, cancellationToken))
            yield return update;
    }
}

internal sealed class DefaultCodeAnalysisStrategy : IAnalysisStrategy<AgentContext, AgentResponse>
{
    private readonly IChatClient _brain;
    private readonly ILogger _logger;
    private readonly HashSet<string> _supportedExtensions;

    private static readonly string[] _codePatterns =
    {
        "code ", "programming", "debug ", "refactor", "compile", "lint ",
        "syntax", "class ", "function ", "import ", "require ", "package",
        ".cs", ".py", ".js", ".ts", ".go", ".rs", ".java", ".kt",
        "github.com", "gitlab", "repository", "commit "
    };

    public string StrategyName => "default-code-analysis";

    public DefaultCodeAnalysisStrategy(IChatClient brain, ILogger logger, HashSet<string> supportedExtensions)
    {
        _brain = brain;
        _logger = logger;
        _supportedExtensions = supportedExtensions;
    }

    public bool CanHandle(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        var lower = query.ToLowerInvariant();
        return _codePatterns.Any(p => lower.Contains(p));
    }

    public async Task<AgentResponse> AnalyzeAsync(AgentContext context, CancellationToken ct)
    {
        var messages = new List<ChatMessage>(context.FullHistory)
        {
            new(ChatRole.User, $"Code analysis: {context.UserQuery}")
        };
        var response = await _brain.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, response.Text ?? ""));
    }
}
