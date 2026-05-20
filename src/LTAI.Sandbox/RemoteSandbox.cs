using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Sandbox;

public sealed class RemoteSandboxConfig
{
    public string BaseUrl { get; set; } = "http://localhost:8000";
    public string ApiKey { get; set; } = "";
    public int RequestTimeoutSeconds { get; set; } = 120;
    public int MaxRetries { get; set; } = 3;
}

public sealed class RemoteSandbox : ISandbox
{
    private readonly HttpClient _http;
    private readonly RemoteSandboxConfig _config;
    private readonly ILogger<RemoteSandbox> _logger;
    private readonly string _sandboxId;
    private readonly JsonSerializerOptions _jsonOptions;

    public string Name => $"remote/{_sandboxId[..8]}";
    public SandboxCapability Capability => SandboxCapability.Python | SandboxCapability.Shell
        | SandboxCapability.NetworkIsolation | SandboxCapability.FilesystemIsolation
        | SandboxCapability.MemoryLimit | SandboxCapability.Timeout;

    public RemoteSandbox(RemoteSandboxConfig config, ILogger<RemoteSandbox>? logger = null)
    {
        _config = config;
        _logger = logger ?? NullLogger<RemoteSandbox>.Instance;
        _http = new HttpClient
        {
            BaseAddress = new Uri(config.BaseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds)
        };
        _http.DefaultRequestHeaders.Add("X-API-Key", config.ApiKey);
        _sandboxId = Guid.NewGuid().ToString("N");
        _jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _http.GetAsync("/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<SandboxResult> ExecuteAsync(SandboxRequest request, CancellationToken cancellationToken = default)
    {
        for (int retry = 0; retry <= _config.MaxRetries; retry++)
        {
            try
            {
                var sandboxId = await CreateSandboxAsync(request, cancellationToken);

                var execResult = await ExecuteInSandboxAsync(sandboxId, request, cancellationToken);

                await DeleteSandboxAsync(sandboxId, cancellationToken);

                return execResult;
            }
            catch (HttpRequestException ex) when (retry < _config.MaxRetries)
            {
                _logger.LogWarning(ex, "Remote sandbox attempt {Attempt} failed, retrying", retry + 1);
                await Task.Delay(1000 * (retry + 1), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Remote sandbox execution failed");
                return new SandboxResult
                {
                    Success = false,
                    Error = $"Remote sandbox error: {ex.Message}",
                    Stderr = ex.Message
                };
            }
        }

        return new SandboxResult { Success = false, Error = "Max retries exceeded" };
    }

    private async Task<string> CreateSandboxAsync(SandboxRequest request, CancellationToken ct)
    {
        var image = request.Language switch
        {
            SandboxLanguage.Python => "python:3.11-slim",
            SandboxLanguage.JavaScript => "node:20-slim",
            SandboxLanguage.CSharp => "mcr.microsoft.com/dotnet/sdk:10.0",
            SandboxLanguage.Shell => "ubuntu:24.04",
            _ => "python:3.11-slim"
        };

        var payload = JsonSerializer.Serialize(new
        {
            image,
            cpu = "0.5",
            memory = $"{request.MemoryLimitMb}Mi",
            timeout = request.TimeoutSeconds,
            read_only_root = request.ReadOnlyFilesystem,
            network = request.NetworkEnabled ? "allow" : "deny"
        }, _jsonOptions);

        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("/api/v1/sandboxes", content, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetString() ?? throw new InvalidOperationException("No sandbox ID");
    }

    private async Task<SandboxResult> ExecuteInSandboxAsync(string sandboxId, SandboxRequest request, CancellationToken ct)
    {
        string command;
        if (!string.IsNullOrWhiteSpace(request.Stdin))
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Code));
            command = request.Language switch
            {
                SandboxLanguage.Python => $"echo '{Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Stdin))}' | base64 -d | python3 -c \"{EscapeShell(request.Code)}\"",
                SandboxLanguage.Shell => $"echo '{encoded}' | base64 -d | bash",
                _ => request.Code
            };
        }
        else
        {
            command = request.Language switch
            {
                SandboxLanguage.Python => $"python3 -c \"{EscapeShell(request.Code)}\"",
                SandboxLanguage.JavaScript => $"node -e \"{EscapeShell(request.Code)}\"",
                SandboxLanguage.Shell => request.Code,
                _ => request.Code
            };
        }

        var payload = JsonSerializer.Serialize(new
        {
            command,
            timeout = request.TimeoutSeconds,
            workdir = request.WorkingDirectory ?? "/workspace"
        }, _jsonOptions);

        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"/api/v1/sandboxes/{sandboxId}/exec", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            return new SandboxResult { Success = false, Error = $"HTTP {(int)response.StatusCode}", Stderr = errorBody };
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        var exitCode = doc.RootElement.TryGetProperty("exit_code", out var ec) ? ec.GetInt32() : -1;
        var stdout = doc.RootElement.TryGetProperty("stdout", out var so) ? so.GetString() ?? "" : "";
        var stderr = doc.RootElement.TryGetProperty("stderr", out var se) ? se.GetString() ?? "" : "";
        var timedOut = doc.RootElement.TryGetProperty("timed_out", out var to) && to.GetBoolean();
        var elapsedMs = doc.RootElement.TryGetProperty("elapsed_ms", out var em) ? em.GetInt64() : 0;

        return new SandboxResult
        {
            Success = exitCode == 0 && !timedOut,
            Stdout = stdout,
            Stderr = stderr,
            ExitCode = exitCode,
            ExecutionTimeMs = elapsedMs,
            TimedOut = timedOut
        };
    }

    private async Task DeleteSandboxAsync(string sandboxId, CancellationToken ct)
    {
        try
        {
            await _http.DeleteAsync($"/api/v1/sandboxes/{sandboxId}", ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete remote sandbox {Id}", sandboxId);
        }
    }

    private static string EscapeShell(string code)
    {
        return code.Replace("\\", "\\\\").Replace("\"", "\\\"")
                   .Replace("\n", "\\n").Replace("\r", "\\r");
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
