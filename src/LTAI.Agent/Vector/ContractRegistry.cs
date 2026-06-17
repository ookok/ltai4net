// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════
//  ContractRegistry — cross-repo API contract detection
//  and Provider↔Consumer matching.
//
//  Inspired by zzet/gortex: auto-detect API contracts
//  (HTTP, gRPC, message topics, env vars) and match
//  providers to consumers across repository boundaries.
// ═══════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace LTAI.Agent.Vector;

public sealed partial class ContractRegistry
{
    private readonly ConcurrentDictionary<string, Contract> _contracts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HashSet<string>> _repoContracts = new(StringComparer.OrdinalIgnoreCase);
    private int _totalMatches;

    /// <summary>Number of registered contracts.</summary>
    public int Count => _contracts.Count;
    /// <summary>Number of Provider↔Consumer matches found.</summary>
    public int Matches => _totalMatches;

    /// <summary>Register a contract from a file scan.</summary>
    public void Register(string repoId, string filePath, ContractType type, string contractId, string side)
    {
        var key = $"{type}::{contractId}";
        var contract = _contracts.GetOrAdd(key, _ => new Contract(type, contractId));

        lock (contract)
        {
            if (side == "provider" || side == "both")
                contract.Providers.Add(repoId);
            if (side == "consumer" || side == "both")
                contract.Consumers.Add(repoId);

            contract.Sources.Add(filePath);
        }

        _repoContracts.AddOrUpdate(repoId,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key },
            (_, set) => { set.Add(key); return set; });
    }

    /// <summary>
    /// Scan a file for API contract declarations.
    /// Detects: HTTP routes, gRPC services, message topics, env vars, OpenAPI specs.
    /// </summary>
    public void ScanFile(string repoId, string filePath, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        // HTTP route detection (ASP.NET, Express, Gin, FastAPI, etc.)
        var httpMatches = HttpRoutePattern().Matches(content);
        foreach (Match m in httpMatches)
        {
            var method = m.Groups[1].Value.ToUpperInvariant();
            var route = m.Groups[2].Value;
            Register(repoId, filePath, ContractType.Http, $"{method}::{route}",
                content.Contains("app.Map") || content.Contains("app.Use") || content.Contains("router.") || content.Contains("@app.")
                    ? "provider" : "consumer");
        }

        // gRPC service detection
        var grpcMatches = GrpcServicePattern().Matches(content);
        foreach (Match m in grpcMatches)
        {
            var service = m.Groups[1].Value;
            Register(repoId, filePath, ContractType.Grpc, $"grpc::{service}",
                filePath.EndsWith(".proto", StringComparison.OrdinalIgnoreCase) ? "provider" : "consumer");
        }

        // Message topic detection (Kafka, RabbitMQ, Redis pub/sub)
        var topicMatches = TopicPattern().Matches(content);
        foreach (Match m in topicMatches)
        {
            var topic = m.Groups[1].Value;
            var isPublish = content.Contains("Publish") || content.Contains("produce") || content.Contains("Send")
                || content.Contains("emit") || m.Value.Contains("=>");
            Register(repoId, filePath, ContractType.MessageTopic, $"topic::{topic}",
                isPublish ? "provider" : "consumer");
        }

        // Environment variable detection
        var envMatches = EnvVarPattern().Matches(content);
        foreach (Match m in envMatches)
        {
            var env = m.Groups[1].Value;
            Register(repoId, filePath, ContractType.EnvVar, $"env::{env}",
                content.Contains("SetEnvironment") || content.Contains("export ") || content.Contains("set ")
                    ? "provider" : "consumer");
        }

        // OpenAPI / Swagger spec detection
        if (content.Contains("openapi:") || content.Contains("swagger:") || content.Contains("\"openapi\""))
        {
            Register(repoId, filePath, ContractType.OpenApi, Path.GetFileName(filePath), "provider");
        }
    }

    /// <summary>Find orphan providers (no consumer in any repo) or orphan consumers (no provider).</summary>
    public List<(string Key, Contract Contract)> FindOrphans()
    {
        var orphans = new List<(string, Contract)>();
        foreach (var (key, contract) in _contracts)
        {
            if (contract.Providers.Count == 0 || contract.Consumers.Count == 0)
                orphans.Add((key, contract));
        }
        return orphans;
    }

    /// <summary>Find all contracts shared between two repos.</summary>
    public List<(string Key, Contract Contract)> FindCrossRepo(string repoA, string repoB)
    {
        if (!_repoContracts.TryGetValue(repoA, out var setA) ||
            !_repoContracts.TryGetValue(repoB, out var setB))
            return [];

        var shared = new List<(string, Contract)>();
        foreach (var key in setA)
        {
            if (setB.Contains(key) && _contracts.TryGetValue(key, out var contract))
                shared.Add((key, contract));
        }
        return shared;
    }

    /// <summary>Human-readable summary for all tracked contracts.</summary>
    public new string ToString()
    {
        if (_contracts.Count == 0) return "No contracts registered.";

        var lines = new List<string>
        {
            $"## Contracts: {_contracts.Count} registered, {_totalMatches} Provider↔Consumer matches\n"
        };

        foreach (var (key, c) in _contracts.OrderBy(kv => kv.Key))
        {
            var prov = c.Providers.Count > 0 ? string.Join(", ", c.Providers) : "(none)";
            var cons = c.Consumers.Count > 0 ? string.Join(", ", c.Consumers) : "(none)";
            lines.Add($"  [{c.Type}] {c.Id}");
            lines.Add($"    Provider: {prov}");
            lines.Add($"    Consumer: {cons}");
        }

        _totalMatches = _contracts.Values.Count(c => c.Providers.Count > 0 && c.Consumers.Count > 0);

        return string.Join("\n", lines);
    }

    /// <summary>Reset all registrations.</summary>
    public void Clear()
    {
        _contracts.Clear();
        _repoContracts.Clear();
        _totalMatches = 0;
    }

    // ── Regex patterns ──

    [GeneratedRegex(@"(?:app\.Map(?:Get|Post|Put|Delete|Patch)|router\.(?:get|post|put|delete|patch)|@(?:app|router)\.(?:get|post|put|delete|patch)|HttpGet|HttpPost|HttpPut|HttpDelete)\s*\(\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled, 500)]
    private static partial Regex HttpRoutePattern();

    [GeneratedRegex(@"service\s+(\w+)\s*\{", RegexOptions.Compiled, 500)]
    private static partial Regex GrpcServicePattern();

    [GeneratedRegex(@"(?:topic|exchange|queue|channel)\s*[:=]\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled, 500)]
    private static partial Regex TopicPattern();

    [GeneratedRegex(@"(?:os\.Getenv|process\.env|GetEnvironmentVariable|environ\.get)\s*\(?\s*[""']([^""']+)[""']", RegexOptions.Compiled, 500)]
    private static partial Regex EnvVarPattern();
}

public enum ContractType { Http, Grpc, MessageTopic, EnvVar, OpenApi, WebSocket }

public sealed class Contract
{
    public ContractType Type { get; }
    public string Id { get; }
    public HashSet<string> Providers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Consumers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Contract(ContractType type, string id)
    {
        Type = type;
        Id = id;
    }
}
