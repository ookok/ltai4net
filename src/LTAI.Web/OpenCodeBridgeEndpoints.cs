using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LTAI.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Web;

public static class OpenCodeBridgeEndpoints
{
    private static readonly string? OpenCodePath = FindOpenCodePath();
    private static bool? _opencodeAvailable;

    private static readonly ConcurrentDictionary<string, OpenCodeSession> _sessions = new();

    private sealed class OpenCodeSession
    {
        public string Id { get; init; } = string.Empty;
        public List<string> Messages { get; init; } = new();
        public DateTime CreatedAt { get; init; }
        public DateTime LastActivity { get; set; }
    }

    private static string? FindOpenCodePath()
    {
        var candidates = new[]
        {
            "opencode",
            "opencode.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "opencode", "opencode.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "opencode", "opencode.exe"),
            "/usr/local/bin/opencode",
            "/usr/bin/opencode"
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    process.WaitForExit(5000);
                    if (process.ExitCode == 0)
                    {
                        _opencodeAvailable = true;
                        return candidate;
                    }
                }
            }
            catch
            {
            }
        }

        _opencodeAvailable = false;
        return null;
    }

    public static void MapOpenCodeBridgeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/opencode/health", () =>
        {
            var available = OpenCodePath != null && (_opencodeAvailable ?? false);
            string? version = null;

            if (available)
            {
                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = OpenCodePath!,
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var process = Process.Start(startInfo);
                    if (process != null)
                    {
                        var output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit(5000);
                        version = output.Trim();
                    }
                }
                catch
                {
                    version = "unknown";
                }
            }

            return Results.Json(new
            {
                opencode_available = available,
                version = version ?? ""
            });
        });

        endpoints.MapPost("/api/opencode/chat", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var request = JsonSerializer.Deserialize<OpenCodeChatRequest>(body);

                if (request == null || string.IsNullOrWhiteSpace(request.Prompt))
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Prompt is required" }));
                    return;
                }

                if (OpenCodePath == null || !(_opencodeAvailable ?? false))
                {
                    var chatClient = endpoints.ServiceProvider.GetService<IChatClient>();
                    string fallbackResponse;

                    if (chatClient != null)
                    {
                        try
                        {
                            var response = await chatClient.GetResponseAsync(request.Prompt);
                            fallbackResponse = response.Text ?? "";
                        }
                        catch
                        {
                            fallbackResponse = $"[Fallback response for: {request.Prompt}]";
                        }
                    }
                    else
                    {
                        fallbackResponse = $"[OpenCode not available. Prompt: {request.Prompt}]";
                    }

                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        response = fallbackResponse,
                        source = "ltai_fallback"
                    }));
                    return;
                }

                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = OpenCodePath,
                        Arguments = $"-p \"{request.Prompt.Replace("\"", "\\\"")}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    if (!string.IsNullOrWhiteSpace(request.Model))
                    {
                        startInfo.Environment["OPENCODE_MODEL"] = request.Model;
                    }

                    using var process = Process.Start(startInfo);
                    if (process == null)
                    {
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Failed to start opencode process" }));
                        return;
                    }

                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit(120000);

                    var sessionId = Guid.NewGuid().ToString("N");
                    var messages = output
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .ToList();

                    _sessions.TryAdd(sessionId, new OpenCodeSession
                    {
                        Id = sessionId,
                        Messages = messages,
                        CreatedAt = DateTime.UtcNow,
                        LastActivity = DateTime.UtcNow
                    });

                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        session_id = sessionId,
                        response = output.Trim(),
                        error = string.IsNullOrWhiteSpace(error) ? null : error
                    }));
                }
                catch (Exception ex)
                {
                    context.Response.StatusCode = 500;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
                }
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        });

        endpoints.MapGet("/api/opencode/session/{id}/message", async (HttpContext context, string id) =>
        {
            context.Response.ContentType = "application/json";

            if (!_sessions.TryGetValue(id, out var session))
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Session not found" }));
                return;
            }

            session.LastActivity = DateTime.UtcNow;
            var lastMessage = session.Messages.LastOrDefault();

            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                session_id = id,
                message = lastMessage,
                message_count = session.Messages.Count
            }));
        });

        endpoints.MapGet("/api/opencode/providers", async (HttpContext context) =>
        {
            context.Response.ContentType = "application/json";
            var providers = new List<OpenCodeProvider>();

            var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

            var opencodeJsonPath = Path.Combine(projectRoot, "opencode.json");
            if (File.Exists(opencodeJsonPath))
            {
                providers.AddRange(ExtractProvidersFromJson(opencodeJsonPath));
            }

            var opencodeJsoncPath = Path.Combine(projectRoot, "opencode.jsonc");
            if (File.Exists(opencodeJsoncPath))
            {
                providers.AddRange(ExtractProvidersFromJson(opencodeJsoncPath));
            }

            var dotOpencodeDir = Path.Combine(projectRoot, ".opencode");
            if (Directory.Exists(dotOpencodeDir))
            {
                foreach (var file in Directory.GetFiles(dotOpencodeDir, "*.json"))
                {
                    providers.AddRange(ExtractProvidersFromJson(file));
                }
                foreach (var file in Directory.GetFiles(dotOpencodeDir, "*.jsonc"))
                {
                    providers.AddRange(ExtractProvidersFromJson(file));
                }
            }

            var envProviders = new Dictionary<string, bool>
            {
                ["openai"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")),
                ["anthropic"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")),
                ["google"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_API_KEY")),
                ["deepseek"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")),
                ["ollama"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OLLAMA_HOST")),
                ["groq"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GROQ_API_KEY")),
                ["xai"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XAI_API_KEY")),
                ["openrouter"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")),
            };

            foreach (var (provider, available) in envProviders)
            {
                if (available && !providers.Any(p => p.ProviderName.Equals(provider, StringComparison.OrdinalIgnoreCase)))
                {
                    providers.Add(new OpenCodeProvider(provider, GetDefaultModel(provider), null, true));
                }
            }

            if (providers.Count == 0)
            {
                providers.Add(new OpenCodeProvider("openai", "gpt-4o", "https://api.openai.com/v1", false));
                providers.Add(new OpenCodeProvider("anthropic", "claude-3-5-sonnet", "https://api.anthropic.com/v1", false));
            }

            var result = providers
                .GroupBy(p => p.ProviderName)
                .Select(g => g.First())
                .Select(p => new
                {
                    provider = p.ProviderName,
                    model = p.Model,
                    is_free = p.IsFree
                })
                .ToList();

            await context.Response.WriteAsync(JsonSerializer.Serialize(result));
        });
    }

    private static string GetDefaultModel(string provider)
    {
        return provider.ToLower() switch
        {
            "openai" => "gpt-4o",
            "anthropic" => "claude-3-5-sonnet-20241022",
            "google" => "gemini-2.0-flash",
            "deepseek" => "deepseek-chat",
            "ollama" => "llama3.1",
            "groq" => "llama-3.1-70b-versatile",
            "xai" => "grok-2",
            "openrouter" => "openai/gpt-4o",
            _ => "default"
        };
    }

    private static List<OpenCodeProvider> ExtractProvidersFromJson(string filePath)
    {
        var results = new List<OpenCodeProvider>();
        try
        {
            var json = File.ReadAllText(filePath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("providers", out var providers))
            {
                foreach (var p in providers.EnumerateArray())
                {
                    var name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var model = p.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                    var endpoint = p.TryGetProperty("endpoint", out var e) ? e.GetString() : null;
                    var isFree = p.TryGetProperty("is_free", out var f) && f.GetBoolean();
                    if (!string.IsNullOrWhiteSpace(name))
                        results.Add(new OpenCodeProvider(name, model, endpoint, isFree));
                }
            }

            if (doc.RootElement.TryGetProperty("models", out var models))
            {
                foreach (var m in models.EnumerateArray())
                {
                    if (m.TryGetProperty("provider", out var prov) &&
                        m.TryGetProperty("name", out var modelName))
                    {
                        var provName = prov.GetString() ?? "";
                        var mName = modelName.GetString() ?? "";
                        var endpoint = m.TryGetProperty("endpoint", out var ep) ? ep.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(provName) && !string.IsNullOrWhiteSpace(mName))
                            results.Add(new OpenCodeProvider(provName, mName, endpoint, false));
                    }
                }
            }
        }
        catch
        {
        }
        return results;
    }
}

public sealed record OpenCodeProvider(
    string ProviderName,
    string Model,
    string? Endpoint,
    bool IsFree
);

public sealed record OpenCodeChatRequest
{
    public string Prompt { get; init; } = string.Empty;
    public string? Model { get; init; }
}
