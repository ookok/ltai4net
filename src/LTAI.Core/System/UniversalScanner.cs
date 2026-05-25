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

            var category = ClassificationRegistry.EndpointCategory.Classify(lowerDesc);

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
                ? ClassificationRegistry.AuthType.Classify(lowerDesc)
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

        if (!IsSafeUrl(url))
        {
            _logger.LogInformation("Blocked probe to unsafe URL: {Url}", url);
            return endpoint with { IsAlive = false };
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var healthResp = await _http.GetAsync($"{url}/health", cts.Token);
            if (healthResp.IsSuccessStatusCode)
            {
                endpoint = endpoint with { IsAlive = true, Protocol = "http" };
                await ProbeOpenAI(endpoint, url, cts.Token).ConfigureAwait(false);
                return endpoint;
            }
        }
        catch { /* non-fatal */ }

        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var openaiEndpoint = await ProbeOpenAI(endpoint, url, cts2.Token).ConfigureAwait(false);
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
            var mcpResp = await _http.PostAsync(url, mcpContent, cts.Token).ConfigureAwait(false);

            if (mcpResp.IsSuccessStatusCode)
            {
                var body = await mcpResp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
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
        catch { /* non-fatal */ }

        return endpoint with { IsAlive = false };
    }

    private async Task<ServiceEndpoint> ProbeOpenAI(ServiceEndpoint endpoint, string url, CancellationToken ct)
    {
        try
        {
            var modelsResp = await _http.GetAsync($"{url}/v1/models", ct);
            if (modelsResp.IsSuccessStatusCode)
            {
                var body = await modelsResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
                catch { /* non-fatal */ }

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
        catch { /* non-fatal */ }

        return endpoint;
    }

    public List<string> AnalyzeCapability(List<string> models)
    {
        var caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            var capability = ClassificationRegistry.ModelCapability.Classify(model);
            if (capability != "general")
                caps.Add(capability);
        }
        return caps.Count == 0 ? new List<string> { "completion" } : caps.ToList();
    }

    public List<string> HeuristicCapability(string url)
    {
        var caps = new List<string>();
        var lower = url.ToLowerInvariant();
        var allMatching = ClassificationRegistry.UrlCapability as MultiKeywordClassifier;

        if (lower.Contains("openai")) caps.AddRange(["chat", "completion", "embedding"]);
        if (lower.Contains("deepseek")) caps.AddRange(["chat", "completion", "code", "reasoning"]);
        if (lower.Contains("qwen")) caps.AddRange(["chat", "completion", "vision"]);
        if (lower.Contains("anthropic") || lower.Contains("claude")) caps.AddRange(["chat", "completion", "code"]);
        if (lower.Contains("gemini") || lower.Contains("google")) caps.AddRange(["chat", "completion", "vision"]);
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
        if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && !host.Equals("127.0.0.1"))
        {
            _logger.LogWarning("ScanNetwork blocked: only localhost is allowed, got '{Host}'", host);
            return new List<ServiceEndpoint>();
        }
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

                    svc = await ProbeProtocol(svc).ConfigureAwait(false);
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
        return _discovered.Values.Where(s =>
            s.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static bool IsSafeUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;

        var host = url;
        var schemeIdx = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
            host = url[(schemeIdx + 3)..];

        var portIdx = host.IndexOf(':');
        if (portIdx >= 0)
            host = host[..portIdx];

        var pathIdx = host.IndexOf('/');
        if (pathIdx >= 0)
            host = host[..pathIdx];

        if (host.Length == 0) return false;

        if (host == "localhost" || host == "127.0.0.1" || host == "[::1]")
            return false;

        if (Uri.CheckHostName(host) == UriHostNameType.IPv4)
        {
            var parts = host.Split('.');
            if (parts.Length != 4) return false;
            if (parts[0] == "10") return false;
            if (parts[0] == "172" && int.TryParse(parts[1], out var b) && b >= 16 && b <= 31) return false;
            if (parts[0] == "192" && parts[1] == "168") return false;
            if (parts[0] == "169" && parts[1] == "254") return false; // link-local
            if (parts[0] == "0") return false;
            if (parts[0] == "127") return false;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
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
