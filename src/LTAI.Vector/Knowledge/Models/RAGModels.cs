namespace LTAI.Vector.Knowledge.Models;

public record Entity(string Id, string Label, Dictionary<string, object>? Properties = null);

public record Triplet(
    string Subject, string Predicate, string Object,
    string SourceText = "", double Confidence = 1.0);

public record RelationRule(
    string Relation,
    bool Transitive = false, bool Symmetric = false, bool Reflexive = false,
    string? Inverse = null);

public record KnowledgeGraphStats(
    int EntityCount, int EdgeCount,
    Dictionary<string, int> ByRelationType, int TransitivePairs, int RulesRegistered);

public record CompressStats(
    int HotCount, int WarmCount, int ColdCount,
    int TotalCompressed, long BytesSaved, double LastCompress);

public record CompressedEntry(
    string Id, string OriginalId, string Tier,
    string Summary, List<string> Keywords, string Timestamp,
    int AccessCount = 0, double LastAccess = 0,
    int OriginalSize = 0, int CompressedSize = 0);

public record CleanResult(
    string Stage, bool Passed, string Reason = "", double QualityScore = 1.0);

public record CleanReport(
    int Total, int Passed, int Rejected,
    Dictionary<string, int> PerStage, double AvgQuality,
    List<Dictionary<string, object>> Rejections);

public record ExcludeRule(
    string Pattern, string Description = "",
    string MatchField = "content", bool Enabled = true,
    int Priority = 0, double CreatedAt = 0,
    int HitCount = 0, double LastHit = 0);

public record EventEntry(
    string Id, string SessionId, string Timestamp,
    string Role, string Content,
    string FactPerspective = "", string RelPerspective = "",
    List<double>? Embedding = null, List<string>? Sources = null,
    string PersonaDomain = "", double EmotionalValence = 0.0)
{
    public string TextForRetrieval() =>
        (!string.IsNullOrEmpty(FactPerspective) || !string.IsNullOrEmpty(RelPerspective))
            ? $"{FactPerspective} {RelPerspective}"
            : Content.Length > 200 ? Content[..200] : Content;
}

public record SynthesisBlock(
    string Id, string Timestamp, string Content,
    List<string> SourceEntries, List<string> SessionIds,
    string ModelCategory = "general", double Confidence = 0.5, int EvidenceCount = 0)
{
    public string TextForRetrieval() => Content;
}

public record MemoryBuffer(
    List<EventEntry> Entries);

public class MutableMemoryBuffer
{
    public List<EventEntry> Entries { get; set; } = new();
    public string FirstTimestamp { get; set; } = "";
    public string LastTimestamp { get; set; } = "";
}

public record Opinion(
    string Text, double Confidence, int EvidenceCount,
    string Category = "general", List<string>? Sources = null,
    string CreatedAt = "", string LastUpdated = "");

public record MentalModel(
    string ModelId, string Name, string Description,
    List<Opinion>? Opinions = null, string Category = "general",
    double Confidence = 0.0, int EvidenceSessions = 0,
    string CreatedAt = "", string LastUpdated = "");

// ── RAG Models ──
public record RankedDocument(
    string DocId, string Text,
    double OriginalScore = 0.0, double RerankScore = 0.0,
    string Source = "", Dictionary<string, object>? Metadata = null)
{
    public double CombinedScore => OriginalScore * 0.3 + RerankScore * 0.7;
}

public record RerankResult(
    string Query, List<RankedDocument> RankedDocs,
    string Method = "heuristic", int TopK = 5,
    double LatencyMs = 0.0, int OriginalCount = 0, int RerankedCount = 0);

public record SubQuery(
    string Query, double Weight = 1.0,
    string Intent = "", List<string>? Dependencies = null);

public record DecomposedQuery(
    string Original, List<SubQuery> SubQueries,
    string HyDeDocument = "", string Strategy = "direct");

public record DecomposedResult(
    string OriginalQuery,
    Dictionary<string, List<object>> SubResults,
    string MergedText = "", string StrategyUsed = "");

public enum RAGMode
{
    Conditional, Iterative, ToolRouting, Planning, Reflective, MultiAgent, Hitl
}

public record RetrievalRound(
    int RoundId, string Query,
    List<string> Sources, int DocCount,
    List<string>? TopDocs = null, string Answer = "",
    double Confidence = 0.0, bool NeedsMore = true,
    string Reasoning = "", double LatencyMs = 0.0, int TokensUsed = 0);

public record AgenticResult(
    string OriginalQuery, List<RetrievalRound> Rounds,
    string FinalAnswer = "", double FinalConfidence = 0.0,
    List<string>? SourcesUsed = null, int TotalRounds = 0,
    int TotalTokens = 0, double TotalMs = 0.0,
    string Mode = "", string Evaluation = "")
{
    public bool IsSatisfactory => FinalConfidence >= 0.7;
}

public class RAGCircuitBreaker(int staleThreshold = 3, double minImprovement = 0.05)
{
    private int _consecutiveStale;
    private double _bestConfidence;
    private int _totalFailures;
    private bool _open;

    public bool Record(double confidence)
    {
        if (confidence <= _bestConfidence + minImprovement)
            _consecutiveStale++;
        else
        { _consecutiveStale = 0; _bestConfidence = confidence; }
        _open = _consecutiveStale >= staleThreshold;
        return _open;
    }

    public bool RecordFailure() { _totalFailures++; _open = _totalFailures >= staleThreshold; return _open; }
    public void Reset() { _consecutiveStale = 0; _bestConfidence = 0; _totalFailures = 0; _open = false; }
    public bool IsOpen => _open;
}

public record KnowledgeSearchRequest(
    string Query, RAGMode Mode = RAGMode.Iterative,
    int MaxRounds = 3, int MaxTokens = 50000, string Domain = "general");

public record KnowledgeSearchResponse(
    AgenticResult? RAGResult, List<KnowledgeSearchResult>? DirectResults,
    string Strategy, double ElapsedMs);
