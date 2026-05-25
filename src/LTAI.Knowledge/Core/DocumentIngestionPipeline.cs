using System.Diagnostics;
using System.Text.Json;
using LTAI.Knowledge.Core.Models;
using LTAI.Knowledge.Document;
using LTAI.Knowledge.Vector.Interfaces;
using Microsoft.Extensions.Logging;

namespace LTAI.Knowledge.Core;

/// <summary>
/// Ingestion orchestrator: UniversalFileParser → KnowledgeBase → KnowledgeGraph → VectorStore.
/// Bridges the previously disconnected ingestion chain.
/// </summary>
public sealed class DocumentIngestionPipeline
{
    private readonly UniversalFileParser _parser;
    private readonly KnowledgeBase _knowledgeBase;
    private readonly KnowledgeGraph _knowledgeGraph;
    private readonly IVectorStore _vectorStore;
    private readonly MarkdownKnowledgeGraph _markdownKG;
    private readonly ILogger<DocumentIngestionPipeline> _logger;

    public DocumentIngestionPipeline(
        UniversalFileParser parser,
        KnowledgeBase knowledgeBase,
        KnowledgeGraph knowledgeGraph,
        IVectorStore vectorStore,
        MarkdownKnowledgeGraph markdownKG,
        ILogger<DocumentIngestionPipeline> logger)
    {
        _parser = parser;
        _knowledgeBase = knowledgeBase;
        _knowledgeGraph = knowledgeGraph;
        _vectorStore = vectorStore;
        _markdownKG = markdownKG;
        _logger = logger;
    }

    public async Task<IngestionResult> IngestFileAsync(string filePath, string domain = "general",
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var steps = new List<string>();

        // Step 1: Parse
        var parseResult = await _parser.ParseAsync(filePath, ct).ConfigureAwait(false);
        if (!parseResult.Success)
        {
            return new IngestionResult { Success = false, Error = parseResult.Error ?? "Parse failed", Steps = steps };
        }
        steps.Add($"parsed:{parseResult.Format}");

        // Step 2: Store in KnowledgeBase
        var title = parseResult.Metadata?.GetValueOrDefault("title")?.ToString()
            ?? Path.GetFileNameWithoutExtension(filePath);
        _knowledgeBase.AddKnowledge(title, parseResult.Text ?? "", domain,
            category: parseResult.Format,
            source: filePath);
        steps.Add("stored:kb");

        // Step 3: Extract triplets → KnowledgeGraph
        var triplets = KnowledgeGraph.ExtractTripletsRegex(parseResult.Text ?? "");
        if (triplets.Count > 0)
        {
            _knowledgeGraph.AddTripletsToGraph(triplets);
            steps.Add($"kg:{triplets.Count}triplets");
        }

        // Step 4: Vector embed (fire-and-forget via KnowledgeBase)
        if (!string.IsNullOrWhiteSpace(parseResult.Text))
            steps.Add("embedded:vector");

        // Step 5: Markdown-specific KG indexing
        if (parseResult.Format is "markdown" or "text")
        {
            try
            {
                _markdownKG.AddOrUpdateFile(filePath, parseResult.Text ?? "");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Markdown KG sync failed for {Path}", filePath);
            }
        }

        sw.Stop();
        _logger.LogInformation("Ingested {Path} ({Format}) in {Ms}ms: {Steps}",
            filePath, parseResult.Format, sw.ElapsedMilliseconds, string.Join("→", steps));

        return new IngestionResult
        {
            Success = true,
            Steps = steps,
            TripletCount = triplets.Count,
            TotalMs = sw.ElapsedMilliseconds
        };
    }

    public async Task<IngestionResult> IngestDirectoryAsync(string dirPath, string domain = "general",
        string pattern = "*.*", CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var results = new List<IngestionResult>();
        var files = Directory.GetFiles(dirPath, pattern, SearchOption.AllDirectories);

        foreach (var file in files.Take(100))
        {
            ct.ThrowIfCancellationRequested();
            var result = await IngestFileAsync(file, domain, ct).ConfigureAwait(false);
            results.Add(result);
        }

        sw.Stop();
        return new IngestionResult
        {
            Success = results.All(r => r.Success),
            Steps = new List<string> { $"batch:{results.Count}/{files.Length}files" },
            TripletCount = results.Sum(r => r.TripletCount),
            TotalMs = sw.ElapsedMilliseconds
        };
    }
}

public sealed class IngestionResult
{
    public bool Success { get; init; }
    public List<string> Steps { get; init; } = new();
    public int TripletCount { get; init; }
    public long TotalMs { get; init; }
    public string? Error { get; init; }
}
