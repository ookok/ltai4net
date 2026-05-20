using System.Collections.Concurrent;
using LTAI.Vector.Interfaces;
using LTAI.Vector.Knowledge.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Vector.Knowledge;

public sealed record CompilerEval
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Question { get; init; } = "";
    public Dictionary<string, string> ExpectedFields { get; init; } = new();
    public double Tolerance { get; init; } = 0.85;
}

public sealed record CompilerIteration
{
    public int Index { get; init; }
    public double EvalScore { get; init; }
    public int CorrectCount { get; init; }
    public int TotalCount { get; init; }
    public string CurationStrategy { get; init; } = "";
    public List<string> DiscoveredFields { get; init; } = new();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class KnowledgeCompiler
{
    private readonly IChatClient _chatClient;
    private readonly ArtifactStore _artifactStore;
    private readonly ProvenanceTracker _provenanceTracker;
    private readonly IVectorStore _vectorStore;
    private readonly AgenticRAG _agenticRAG;
    private readonly ILogger<KnowledgeCompiler>? _logger;

    private readonly ConcurrentDictionary<string, List<CompilerIteration>> _iterationLogs = new();
    private const int MaxIterations = 10;
    private const int MinEvals = 3;
    private const double ConvergenceThreshold = 0.05;

    public KnowledgeCompiler(
        IChatClient chatClient,
        ArtifactStore artifactStore,
        ProvenanceTracker provenanceTracker,
        IVectorStore vectorStore,
        AgenticRAG agenticRAG,
        ILogger<KnowledgeCompiler>? logger = null)
    {
        _chatClient = chatClient;
        _artifactStore = artifactStore;
        _provenanceTracker = provenanceTracker;
        _vectorStore = vectorStore;
        _agenticRAG = agenticRAG;
        _logger = logger;
    }

    public async Task<KnowledgeArtifact?> CompileAsync(
        string domain, string taskDescription,
        List<CompilerEval> evals,
        List<string> sourceDocumentIds,
        CancellationToken ct = default)
    {
        var artifactId = $"art_{domain}_{Guid.NewGuid():N}"[..16];
        _logger?.LogInformation(
            "KnowledgeCompiler: starting compile domain={Domain} task={Task} evals={Evals}",
            domain, taskDescription, evals.Count);

        string curationStrategy = await DiscoverInitialStrategy(domain, taskDescription, ct);
        var discoveredFields = await DiscoverFields(domain, taskDescription, evals, ct);

        var iterations = new List<CompilerIteration>();
        double bestScore = 0;
        KnowledgeArtifact? bestArtifact = null;
        int staleCount = 0;

        for (int iter = 1; iter <= MaxIterations && staleCount < 3; iter++)
        {
            var compiled = await CompileIteration(
                artifactId, domain, taskDescription,
                curationStrategy, discoveredFields,
                sourceDocumentIds, ct);

            var (score, correct, total) = await EvaluateArtifact(compiled, evals, ct);

            var record = new CompilerIteration
            {
                Index = iter,
                EvalScore = score,
                CorrectCount = correct,
                TotalCount = total,
                CurationStrategy = curationStrategy,
                DiscoveredFields = discoveredFields
            };
            iterations.Add(record);

            _logger?.LogInformation(
                "KnowledgeCompiler: iter={Iter} score={Score:F3} correct={Correct}/{Total}",
                iter, score, correct, total);

            if (score > bestScore + ConvergenceThreshold)
            {
                bestScore = score;
                bestArtifact = compiled with { EvalScore = score };
                staleCount = 0;
                _artifactStore.StoreArtifact(bestArtifact);
                _provenanceTracker.TrackVersion(artifactId, compiled.Version, score);
            }
            else
            {
                staleCount++;
            }

            if (score >= 0.9) break;

            curationStrategy = await RefineStrategy(
                curationStrategy, score, correct, total, evals.Count, ct);
            discoveredFields = await DiscoverFields(domain, taskDescription, evals, ct);
        }

        _iterationLogs[artifactId] = iterations;

        _logger?.LogInformation(
            "KnowledgeCompiler: completed domain={Domain} bestScore={Score:F3} iters={Iters}",
            domain, bestScore, iterations.Count);

        return bestArtifact;
    }

    private async Task<string> DiscoverInitialStrategy(
        string domain, string taskDescription, CancellationToken ct)
    {
        var prompt = $"""
            You are a knowledge compiler. Design a curation strategy for domain '{domain}'.
            Task: {taskDescription}
            
            Output a JSON object with:
            - extraction_method: how to extract structured fields from raw documents
            - field_priorities: priority-ordered field types to extract
            - chunking_strategy: how to segment documents
            - confidence_model: how to score field confidence
            
            Return only valid JSON, no markdown.
            """;

        var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: ct);
        return response.Text;
    }

    private async Task<List<string>> DiscoverFields(
        string domain, string taskDescription,
        List<CompilerEval> evals, CancellationToken ct)
    {
        var expectedFields = evals
            .SelectMany(e => e.ExpectedFields.Keys)
            .Distinct()
            .ToList();

        if (expectedFields.Count >= 3)
            return expectedFields;

        var prompt = $"""
            Domain '{domain}': {taskDescription}
            
            Given this domain, list all relevant structured fields that should be extracted.
            Return as JSON array of strings. Include field name, data type hint in parentheses.
            Example: ["company_name (string)", "revenue_usd (number)", "fiscal_year (number)"]
            """;

        var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: ct);
        return ParseFieldList(response.Text, expectedFields);
    }

    private async Task<KnowledgeArtifact> CompileIteration(
        string artifactId, string domain, string taskDescription,
        string curationStrategy, List<string> discoveredFields,
        List<string> sourceDocumentIds, CancellationToken ct)
    {
        var fields = new List<ArtifactField>();

        foreach (var fieldSpec in discoveredFields)
        {
            var (name, type) = ParseFieldSpec(fieldSpec);
            var searchQuery = $"{taskDescription} {name} {domain}";
            var searchResults = _agenticRAG.Search(searchQuery, RAGMode.Iterative, domain: domain);
            var knResults = searchResults.ToList();

            if (knResults.Count == 0) continue;

            var evidencePrompt = string.Join("\n",
                $"Extract the value of field '{name}' from the following context.",
                $"Task: {taskDescription}",
                "",
                "Context:",
                string.Join("\n", knResults.Select(r => r.Content)),
                "",
                "Return JSON: {\"value\": \"...\", \"confidence\": 0.0-1.0, \"evidence\": \"...\"}"
            );

            var response = await _chatClient.GetResponseAsync(evidencePrompt, cancellationToken: ct);
            var (value, confidence, evidence) = ParseEvidenceResponse(response.Text);

            var citations = new List<SourceCitation>();
            foreach (var result in knResults)
            {
                citations.Add(new SourceCitation
                {
                    SourceId = result.Source,
                    SourcePath = result.Source,
                    ChunkId = result.Id,
                    Confidence = result.Score,
                    EvidenceSnippet = result.Content.Length > 200
                        ? result.Content[..200] : result.Content
                });

                _provenanceTracker.TrackField($"{artifactId}::{name}", citations.Last());
            }

            fields.Add(new ArtifactField
            {
                Name = name,
                Value = value,
                Type = type,
                Citations = citations,
                Confidence = confidence,
                Metadata = new()
                {
                    ["extraction_method"] = "llm_knowledge_compiler",
                    ["domain"] = domain
                }
            });
        }

        return new KnowledgeArtifact
        {
            Id = artifactId,
            Name = $"{domain}_artifact",
            Domain = domain,
            Description = taskDescription,
            Fields = fields,
            CompiledFor = domain,
            EvalScore = 0,
            Tags = new()
            {
                ["domain"] = domain,
                ["compiler"] = "knowledge_compiler_v1"
            }
        };
    }

    private async Task<(double score, int correct, int total)> EvaluateArtifact(
        KnowledgeArtifact artifact, List<CompilerEval> evals, CancellationToken ct)
    {
        if (evals.Count == 0) return (0, 0, 0);

        int correct = 0;
        foreach (var eval in evals)
        {
            var prompt = string.Join("\n",
                "Evaluate whether the extracted artifact satisfies the question.",
                "",
                $"Question: {eval.Question}",
                $"Expected fields: {string.Join(", ", eval.ExpectedFields.Select(kv => $"{kv.Key}={kv.Value}"))}",
                "",
                "Artifact fields:",
                string.Join("\n", artifact.Fields.Where(f => eval.ExpectedFields.ContainsKey(f.Name))
                    .Select(f => $"  {f.Name} = {f.Value} (confidence: {f.Confidence:F2})")),
                "",
                "For each expected field, compare the artifact's value against expected value.",
                "Consider it correct if semantic meaning matches (not necessarily exact string match).",
                "",
                "Return JSON: {\"correct_count\": N, \"total_fields\": N, \"details\": [{\"field\": \"...\", \"correct\": true/false, \"reason\": \"...\"}]}"
            );

            var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: ct);
            var (c, _) = ParseEvalResponse(response.Text);
            correct += c;
        }

        int total = evals.Count * Math.Max(1, evals[0].ExpectedFields.Count);
        double score = total > 0 ? (double)correct / total : 0;
        return (Math.Min(score, 1.0), correct, total);
    }

    private async Task<string> RefineStrategy(
        string currentStrategy, double score,
        int correct, int total, int totalEvals, CancellationToken ct)
    {
        if (score >= 0.85) return currentStrategy;

        var prompt = $"""
            Current curation strategy achieved score {score:F2} ({correct}/{total} correct).
            
            Current strategy:
            {currentStrategy}
            
            Analyze failures and output improved curation strategy as JSON with same structure.
            Focus on what fields were incorrectly extracted and how to fix the extraction method.
            Return only valid JSON.
            """;

        var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: ct);
        return response.Text.Length > 50 ? response.Text : currentStrategy;
    }

    public List<CompilerIteration> GetIterationLog(string artifactId)
    {
        _iterationLogs.TryGetValue(artifactId, out var log);
        return log ?? new();
    }

    private static (string name, string type) ParseFieldSpec(string spec)
    {
        var match = System.Text.RegularExpressions.Regex.Match(spec,
            @"(\w+)\s*\((\w+)\)");
        if (match.Success)
            return (match.Groups[1].Value, match.Groups[2].Value);
        return (spec.Split('(')[0].Trim(), "string");
    }

    private static List<string> ParseFieldList(string text, List<string> existing)
    {
        try
        {
            var clean = text.Trim();
            if (clean.StartsWith("["))
            {
                return System.Text.Json.JsonSerializer
                    .Deserialize<List<string>>(clean) ?? existing;
            }
        }
        catch { }
        return existing;
    }

    private static (string value, double confidence, string evidence) ParseEvidenceResponse(string text)
    {
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;
            return (
                root.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "",
                root.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0.5,
                root.TryGetProperty("evidence", out var e) ? e.GetString() ?? "" : ""
            );
        }
        catch { return ("", 0.5, ""); }
    }

    private static (int correct, int total) ParseEvalResponse(string text)
    {
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;
            var correct = root.TryGetProperty("correct_count", out var c) ? c.GetInt32() : 0;
            var total = root.TryGetProperty("total_fields", out var t) ? t.GetInt32() : 1;
            return (correct, total);
        }
        catch { return (0, 1); }
    }
}
