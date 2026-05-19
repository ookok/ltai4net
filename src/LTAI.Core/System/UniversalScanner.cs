using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.System;

public sealed record ServiceEndpoint
{
    public string Name { get; init; } = "";
    public string Category { get; init; } = "unknown";
    public string Url { get; init; } = "";
    public string Protocol { get; init; } = "http";
    public bool IsAlive { get; init; }
    public bool RequiresAuth { get; init; }
    public string? AuthType { get; init; }
    public List<string> Models { get; init; } = new();
    public List<string> Capabilities { get; init; } = new();
    public string? ConfigSnippet { get; init; }
    public DateTime DiscoveredAt { get; init; } = DateTime.UtcNow;
}

public sealed class UniversalScanner
{
    public static UniversalScanner Instance => _instance.Value;
    private static readonly Lazy<UniversalScanner> _instance = new(() => new UniversalScanner());

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, ServiceEndpoint> _discovered = new();
    private readonly ILogger<UniversalScanner> _logger;

    public UniversalScanner(ILogger<UniversalScanner>? logger = null)
    {
        _logger = logger ?? NullLogger<UniversalScanner>.Instance;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.Add("User-Agent", "LTAI-UniversalScanner/1.0");
    }

    public List<ServiceEndpoint> DiscoverFromDescription(string description)
    {
        var results = new List<ServiceEndpoint>();
        if (string.IsNullOrWhiteSpace(description)) return results;

        var urlMatches = Regex.Matches(description,
            @"https?://[^\s,;\)]+|(?<!\w)(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})(:\d{1,5})?|[a-zA-Z0-9][-a-zA-Z0-9]*(?:\.[a-zA-Z0-9][-a-zA-Z0-9]*)+\.[a-zA-Z]{2,}(:\d{1,5})?(/[^\s,;\)]*)?");

        var lowerDesc = description.ToLowerInvariant();

        foreach (Match m in urlMatches)
        {
            var url = m.Value.TrimEnd('.', ',', ';', ')');
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = "http://" + url;

            var category = "api";
            if (lowerDesc.Contains("openai") || lowerDesc.Contains("llm") || lowerDesc.Contains("language model"))
                category = "llm";
            else if (lowerDesc.Contains("database") || lowerDesc.Contains("postgres") || lowerDesc.Contains("mysql") || lowerDesc.Contains("redis"))
                category = "database";
            else if (lowerDesc.Contains("mcp") || lowerDesc.Contains("tool"))
                category = "mcp";
            else if (lowerDesc.Contains("weather") || lowerDesc.Contains("news"))
                category = "utility";
            else if (lowerDesc.Contains("graph") || lowerDesc.Contains("knowledge"))
                category = "knowledge";
            else if (lowerDesc.Contains("storage") || lowerDesc.Contains("file"))
                category = "storage";

            var portMatch = Regex.Match(url, @":(\d{1,5})");
            var protocol = "http";
            if (portMatch.Success && int.TryParse(portMatch.Groups[1].Value, out var port))
            {
                protocol = port switch
                {
                    443 => "https",
                    50051 => "grpc",
                    _ => "http"
                };
            }

            var needsAuth = lowerDesc.Contains("api key") || lowerDesc.Contains("token")
                || lowerDesc.Contains("bearer") || lowerDesc.Contains("auth")
                || lowerDesc.Contains("apikey");

            var authType = needsAuth
                ? (lowerDesc.Contains("bearer") ? "bearer" : lowerDesc.Contains("token") ? "token" : "api_key")
                : null;

            var name = Regex.Replace(url, @"^https?://", "");
            name = Regex.Replace(name, @"[/:]", "_").Trim('_');
            if (name.Length > 64) name = name[..64];

            var heuristicCaps = HeuristicCapability(url);

            results.Add(new ServiceEndpoint
            {
                Name = name,
                Category = category,
                Url = url,
                Protocol = protocol,
                RequiresAuth = needsAuth,
                AuthType = authType,
                Capabilities = heuristicCaps,
                ConfigSnippet = $"{{\"endpoint\": \"{url}\", \"auth\": {(needsAuth ? "\"<token>\"" : "null")}}}",
                DiscoveredAt = DateTime.UtcNow
            });
        }

        if (results.Count == 0 && !string.IsNullOrWhiteSpace(description))
        {
            results.Add(new ServiceEndpoint
            {
                Name = "text_parsed_service",
                Category = "api",
                Url = "http://localhost:8080",
                Protocol = "http",
                ConfigSnippet = description[..Math.Min(200, description.Length)],
                DiscoveredAt = DateTime.UtcNow
            });
        }

        return results;
    }

    public async Task<ServiceEndpoint> ProbeProtocol(ServiceEndpoint svc)
    {
        var endpoint = svc with { };
        var url = endpoint.Url.TrimEnd('/');

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var healthResp = await _http.GetAsync($"{url}/health", cts.Token);
            if (healthResp.IsSuccessStatusCode)
            {
                endpoint = endpoint with { IsAlive = true, Protocol = "http" };
                await ProbeOpenAI(endpoint, url, cts.Token);
                return endpoint;
            }
        }
        catch { }

        var openaiEndpoint = await ProbeOpenAI(endpoint, url, CancellationToken.None);
        if (openaiEndpoint.IsAlive) return openaiEndpoint;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var mcpPayload = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "tools/list",
                id = 1
            });
            var mcpContent = new StringContent(mcpPayload, global::System.Text.Encoding.UTF8, new global::System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));
            var mcpResp = await _http.PostAsync(url, mcpContent, cts.Token);

            if (mcpResp.IsSuccessStatusCode)
            {
                var body = await mcpResp.Content.ReadAsStringAsync(cts.Token);
                if (body.Contains("tools") || body.Contains("jsonrpc"))
                {
                    endpoint = endpoint with
                    {
                        IsAlive = true,
                        Protocol = body.Contains("mcp") ? "mcp" : "jsonrpc",
                        Capabilities = endpoint.Capabilities.Concat(new[] { "rpc" }).Distinct().ToList()
                    };
                    return endpoint;
                }
            }
        }
        catch { }

        return endpoint with { IsAlive = false };
    }

    private async Task<ServiceEndpoint> ProbeOpenAI(ServiceEndpoint endpoint, string url, CancellationToken ct)
    {
        try
        {
            var modelsResp = await _http.GetAsync($"{url}/v1/models", ct);
            if (modelsResp.IsSuccessStatusCode)
            {
                var body = await modelsResp.Content.ReadAsStringAsync(ct);
                var models = new List<string>();
                try
                {
                    var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in data.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var id))
                                models.Add(id.GetString() ?? "");
                        }
                    }
                }
                catch { }

                if (models.Count > 0)
                {
                    return endpoint with
                    {
                        IsAlive = true,
                        Protocol = "openai",
                        Models = models,
                        Category = "llm",
                        Capabilities = endpoint.Capabilities
                            .Concat(AnalyzeCapability(models))
                            .Concat(new[] { "chat", "openai_compatible" })
                            .Distinct()
                            .ToList(),
                        RequiresAuth = modelsResp.StatusCode != global::System.Net.HttpStatusCode.OK
                            || body.Contains("error", StringComparison.OrdinalIgnoreCase)
                    };
                }
            }
        }
        catch { }

        return endpoint;
    }

    public List<string> AnalyzeCapability(List<string> models)
    {
        var caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            var lower = model.ToLowerInvariant();
            if (lower.Contains("gpt") || lower.Contains("claude") || lower.Contains("deepseek")
                || lower.Contains("qwen") || lower.Contains("llama") || lower.Contains("gemini"))
                caps.Add("completion");
            if (lower.Contains("code") || lower.Contains("coder") || lower.Contains("copilot"))
                caps.Add("code");
            if (lower.Contains("embed") || lower.Contains("bge") || lower.Contains("e5"))
                caps.Add("embedding");
            if (lower.Contains("vision") || lower.Contains("vl") || lower.Contains("multimodal"))
                caps.Add("vision");
            if (lower.Contains("audio") || lower.Contains("whisper") || lower.Contains("tts"))
                caps.Add("audio");
            if (lower.Contains("image") || lower.Contains("dalle") || lower.Contains("stable"))
                caps.Add("image_generation");
            if (lower.Contains("reason") || lower.Contains("o1") || lower.Contains("o3"))
                caps.Add("reasoning");
        }
        return caps.Count == 0 ? new List<string> { "completion" } : caps.ToList();
    }

    public List<string> HeuristicCapability(string url)
    {
        var caps = new List<string>();
        var lower = url.ToLowerInvariant();

        if (lower.Contains("openai")) caps.AddRange(new[] { "chat", "completion", "embedding" });
        if (lower.Contains("deepseek")) caps.AddRange(new[] { "chat", "completion", "code", "reasoning" });
        if (lower.Contains("qwen")) caps.AddRange(new[] { "chat", "completion", "vision" });
        if (lower.Contains("anthropic") || lower.Contains("claude")) caps.AddRange(new[] { "chat", "completion", "code" });
        if (lower.Contains("gemini") || lower.Contains("google")) caps.AddRange(new[] { "chat", "completion", "vision" });
        if (lower.Contains("ollama") || lower.Contains("local")) caps.Add("chat");
        if (lower.Contains("embed") || lower.Contains("vector")) caps.Add("embedding");
        if (lower.Contains("graph") || lower.Contains("neo4j")) caps.Add("graph");
        if (lower.Contains("weather")) caps.Add("weather");
        if (lower.Contains("search") || lower.Contains("tavily")) caps.Add("search");
        if (lower.Contains("db") || lower.Contains("sql") || lower.Contains("mongo") || lower.Contains("redis"))
            caps.Add("database");

        return caps.Count == 0 ? new List<string> { "api" } : caps.Distinct().ToList();
    }

    public async Task<List<ServiceEndpoint>> ScanNetwork(string host, (int Start, int End) portRange, int maxPorts = 50)
    {
        var discovered = new List<ServiceEndpoint>();
        var scanned = 0;

        var commonPorts = new[] { 80, 443, 8080, 3000, 5000, 8000, 8888, 9090, 11434, 1234, 4891 }
            .Where(p => p >= portRange.Start && p <= portRange.End)
            .ToList();

        var additionalPorts = Enumerable.Range(portRange.Start, portRange.End - portRange.Start + 1)
            .Where(p => !commonPorts.Contains(p))
            .OrderBy(_ => global::System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue))
            .Take(maxPorts - commonPorts.Count);

        var portsToScan = commonPorts.Concat(additionalPorts).Distinct().Take(maxPorts);

        foreach (var port in portsToScan)
        {
            if (scanned >= maxPorts) break;
            scanned++;

            try
            {
                using var tcp = new TcpClient();
                var connectTask = tcp.ConnectAsync(host, port);
                if (await Task.WhenAny(connectTask, Task.Delay(1000)) == connectTask && tcp.Connected)
                {
                    var protocol = port == 443 ? "https" : "http";
                    var url = $"{protocol}://{host}:{port}";

                    var svc = new ServiceEndpoint
                    {
                        Name = $"{host}_{port}",
                        Category = "unknown",
                        Url = url,
                        Protocol = protocol,
                        DiscoveredAt = DateTime.UtcNow
                    };

                    svc = await ProbeProtocol(svc);
                    svc = svc with
                    {
                        Capabilities = svc.Capabilities.Concat(HeuristicCapability(url)).Distinct().ToList()
                    };

                    _discovered[svc.Name] = svc;
                    discovered.Add(svc);
                    _logger.LogInformation("Discovered service: {Name} at {Url} ({Protocol})",
                        svc.Name, svc.Url, svc.Protocol);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Port {Port} closed: {Message}", port, ex.Message);
            }
        }

        return discovered;
    }

    public void AutoRegisterService(ServiceEndpoint svc, string? apiKey = null)
    {
        var key = svc.Name;
        _discovered[key] = svc with
        {
            RequiresAuth = !string.IsNullOrEmpty(apiKey),
            AuthType = !string.IsNullOrEmpty(apiKey) ? "api_key" : svc.AuthType
        };
        _logger.LogInformation("Auto-registered service: {Name} ({Category})", svc.Name, svc.Category);
    }

    public List<ServiceEndpoint> GetDiscovered()
    {
        return _discovered.Values.OrderByDescending(s => s.DiscoveredAt).ToList();
    }

    public List<ServiceEndpoint> GetByCategory(string category)
    {
        return _discovered.Values
            .Where(s => s.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.DiscoveredAt)
            .ToList();
    }

    public Dictionary<string, object> Stats()
    {
        var all = _discovered.Values.ToList();
        return new Dictionary<string, object>
        {
            ["discoveredCount"] = all.Count,
            ["aliveCount"] = all.Count(s => s.IsAlive),
            ["categories"] = all.GroupBy(s => s.Category)
                .ToDictionary(g => g.Key, g => (object)g.Count()),
            ["protocols"] = all.GroupBy(s => s.Protocol)
                .ToDictionary(g => g.Key, g => (object)g.Count()),
            ["totalModels"] = all.Sum(s => s.Models.Count),
            ["withAuth"] = all.Count(s => s.RequiresAuth)
        };
    }
}
