using System.Collections.Concurrent;
using LTAI.Core.System;
using LTAI.Vector.Knowledge;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.TreeLLM.Prompting;

public enum OdeMode { SFT, RL }

public enum RubricDimension
{
    InformationComplexity,
    VisualDependency,
    ShortcutLeakage,
    Verifiability,
    StepAppropriateness,
    ToolUsageQuality,
    ToolPatternDiversity,
    CapabilityRequirement,
    DifficultyMatch,
    LearningUtility
}

public enum FailureStage { None, SeedProposal, WebExploration, GraphOrganization, TaskCuration }

public sealed record OdeSeed(string Entity, string ImageDescription, string Domain, int Difficulty)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
}

public sealed record OdeNode(
    string Id,
    string Entity,
    List<string> Facts,
    List<string> SourceUrls,
    List<string> ImageRefs,
    Dictionary<string, string> Relations)
{
    public bool HasImages => ImageRefs.Count > 0;
}

public sealed record OdeEvidenceGraph(
    string SeedId,
    List<OdeNode> Nodes,
    List<(string From, string To, string Label)> Edges,
    List<OdeNode> DerivedReasoningNodes,
    List<OdeNode> DerivedPerceptionNodes)
{
    public int TotalNodes => Nodes.Count + DerivedReasoningNodes.Count + DerivedPerceptionNodes.Count;
}

public sealed record OdeCandidateTask(
    string Question,
    string InitialImageRef,
    string Answer,
    List<(string Dim, double Score)> Annotations,
    int Difficulty,
    string DifficultyLevel,
    List<string> RequiredCapabilities,
    List<string> PlannedSteps)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
}

public sealed record OdeTraceDiagnosis(
    string TaskId,
    bool Success,
    double RubricScore,
    Dictionary<RubricDimension, double> DimensionScores,
    List<FailureStage> FailureStages,
    string Summary)
{
    public double GetScore(RubricDimension dim)
        => DimensionScores.TryGetValue(dim, out var s) ? s : 0;
}

public sealed record OdeRoundConfig(
    int Round,
    int TotalSeedBudget,
    int ImageBearingNodeBudget,
    int MaxSearchBreadth,
    int MaxExplorationDepth,
    double ImageNodeShare,
    Dictionary<int, double> DifficultyWeights,
    List<string> EnhancementPrompts,
    List<string> ValidationConstraints)
{
    public static OdeRoundConfig Default(int round) => new(
        Round: round,
        TotalSeedBudget: 30,
        ImageBearingNodeBudget: 20,
        MaxSearchBreadth: 5,
        MaxExplorationDepth: 3,
        ImageNodeShare: 0.4,
        DifficultyWeights: new() { [1] = 0.4, [2] = 0.3, [3] = 0.2, [4] = 0.1 },
        EnhancementPrompts: new(),
        ValidationConstraints: new()
        {
            "no_tool_hints_in_question",
            "unambiguous_answer",
            "image_reference_resolved"
        });
}

public sealed record OdeSystemConfig(List<string> EvaluationProtocol, int MaxConcurrentVerification)
{
    public static OdeSystemConfig Default => new(
        new() { "llm_judge_answer_match", "tool_trace_completeness" },
        MaxConcurrentVerification: 4);
}

public sealed record OdeEvolutionResult(
    int CompletedRounds,
    List<OdeCandidateTask> SftDemonstrations,
    List<OdeCandidateTask> RlTasks,
    OdeRoundConfig FinalConfig,
    Dictionary<int, double> RoundPassRates,
    Dictionary<int, Dictionary<RubricDimension, double>> RubricHistory,
    double ElapsedMs);

public sealed class OnPolicyDataEvolver
{
    private readonly IChatClient _chatClient;
    private readonly AgenticRAG _agenticRAG;
    private readonly ILogger<OnPolicyDataEvolver>? _logger;

    private OdeRoundConfig _evolvableConfig = OdeRoundConfig.Default(0);
    private readonly OdeSystemConfig _systemConfig = OdeSystemConfig.Default;

    private readonly ConcurrentDictionary<string, OdeCandidateTask> _candidatePool = new();
    private readonly List<OdeTraceDiagnosis> _diagnosisHistory = new();
    private readonly ConcurrentDictionary<int, double> _roundPassRates = new();
    private readonly ConcurrentDictionary<int, Dictionary<RubricDimension, double>> _rubricHistory = new();
    private readonly List<string> _usedEntities = new();
    private readonly object _entityLock = new();

    private int _currentRound;

    private static readonly string[] Domains = new[]
    {
        "geography", "history", "science", "technology", "art",
        "sports", "medicine", "biology", "economics", "law", "literature"
    };

    private static readonly string[] CapabilityProfiles = new[]
    {
        "perception_only", "perception+search", "perception+reasoning",
        "perception+search+reasoning"
    };

    public OnPolicyDataEvolver(
        IChatClient chatClient,
        AgenticRAG agenticRAG,
        ILogger<OnPolicyDataEvolver>? logger = null)
    {
        _chatClient = chatClient;
        _agenticRAG = agenticRAG;
        _logger = logger;
    }

    public async Task<OdeEvolutionResult> EvolveAsync(
        int maxRounds = 5,
        OdeMode mode = OdeMode.SFT,
        int samplesPerRound = 30,
        CancellationToken cancellationToken = default)
    {
        var start = DateTimeOffset.UtcNow;
        var allSftDemos = new List<OdeCandidateTask>();
        var allRlTasks = new List<OdeCandidateTask>();

        for (int r = 0; r < maxRounds; r++)
        {
            _currentRound = r;
            _evolvableConfig = OdeRoundConfig.Default(r);

            _logger?.LogInformation("ODE Round {Round}/{Max} mode={Mode}", r + 1, maxRounds, mode);

            var tasks = ForwardCuration(samplesPerRound, cancellationToken);
            if (tasks.Count == 0)
            {
                _logger?.LogWarning("ODE Round {Round} produced 0 tasks, stopping", r);
                break;
            }

            var diagnoses = await BackwardOptimization(tasks, mode, cancellationToken);
            _diagnosisHistory.AddRange(diagnoses);

            var passRate = diagnoses.Count(d => d.Success) / (double)Math.Max(1, diagnoses.Count);
            _roundPassRates[r] = passRate;

            var rubricAvg = new Dictionary<RubricDimension, double>();
            foreach (var dim in Enum.GetValues<RubricDimension>())
            {
                var scores = diagnoses.Select(d => d.GetScore(dim)).Where(s => s > 0).ToList();
                rubricAvg[dim] = scores.Count > 0 ? scores.Average() : 0;
            }
            _rubricHistory[r] = rubricAvg;

            foreach (var (task, diag) in tasks.Zip(diagnoses))
            {
                if (mode == OdeMode.SFT && diag.Success)
                    allSftDemos.Add(task with { Annotations = task.Annotations
                        .Append(("pass_" + r, diag.RubricScore)).ToList() });
                else if (mode == OdeMode.RL)
                    allRlTasks.Add(task);
            }

            ApplyRubricOptimization(diagnoses, mode);

            _logger?.LogInformation(
                "ODE Round {Round} pass_rate={PassRate:F2} avg_rubric={AvgRubric:F2}",
                r, passRate, rubricAvg.Values.Average());
        }

        var elapsed = (DateTimeOffset.UtcNow - start).TotalMilliseconds;
        return new OdeEvolutionResult(
            _currentRound + 1,
            allSftDemos,
            allRlTasks,
            _evolvableConfig,
            _roundPassRates.ToDictionary(kv => kv.Key, kv => kv.Value),
            _rubricHistory.ToDictionary(kv => kv.Key, kv => kv.Value),
            elapsed);
    }

    public List<OdeCandidateTask> ForwardCuration(int targetCount, CancellationToken cancellationToken)
    {
        var tasks = new List<OdeCandidateTask>();

        var cfg = _evolvableConfig;
        var seeds = ProposeSeeds(cfg.TotalSeedBudget, cancellationToken);
        if (seeds.Count == 0) return tasks;

        foreach (var seed in seeds)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (tasks.Count >= targetCount) break;

            var nodes = ExploreWeb(seed, cfg, cancellationToken);
            var graph = OrganizeGraph(seed, nodes);
            var candidate = CurateTask(graph, seed, cfg);
            if (candidate != null && ValidateTask(candidate))
            {
                _candidatePool[candidate.Id] = candidate;
                tasks.Add(candidate);
            }
        }

        return tasks;
    }

    private List<OdeSeed> ProposeSeeds(int budget, CancellationToken cancellationToken)
    {
        var seeds = new List<OdeSeed>();
        var cfg = _evolvableConfig;

        for (int i = 0; i < budget && !cancellationToken.IsCancellationRequested; i++)
        {
            var domain = Domains[Random.Shared.Next(Domains.Length)];
            var profile = CapabilityProfiles[Random.Shared.Next(CapabilityProfiles.Length)];

            int difficulty;
            var roll = Random.Shared.NextDouble();
            var cumulative = 0.0;
            difficulty = 1;
            foreach (var (d, w) in cfg.DifficultyWeights.OrderBy(kv => kv.Key))
            {
                cumulative += w;
                if (roll <= cumulative) { difficulty = d; break; }
            }

            var entity = GenerateSeedEntity(domain, cancellationToken);
            if (string.IsNullOrEmpty(entity) || _usedEntities.Contains(entity))
                continue;

            lock (_entityLock)
            {
                if (_usedEntities.Contains(entity)) continue;
                _usedEntities.Add(entity);
            }

            var seed = new OdeSeed(entity, $"Image of {entity}", domain, difficulty);
            seeds.Add(seed);
        }

        return seeds;
    }

    private string GenerateSeedEntity(string domain, CancellationToken cancellationToken)
    {
        try
        {
            var prompt = $"Generate ONE specific real-world entity name for the domain '{domain}'. "
                       + $"Choose an entity that can have visual evidence (photo, chart, map, diagram). "
                       + $"Respond with ONLY the entity name, nothing else.";

            var response = _chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken)
                .GetAwaiter().GetResult();

            var entity = response.Text?.Trim() ?? "";
            if (entity.Length > 100) entity = entity[..100];
            return entity;
        }
        catch
        {
            return $"Random_{domain}_{Random.Shared.Next(1000)}";
        }
    }

    private List<OdeNode> ExploreWeb(OdeSeed seed, OdeRoundConfig cfg, CancellationToken cancellationToken)
    {
        var nodes = new List<OdeNode>();
        var explored = new HashSet<string>();
        var imageBudget = cfg.ImageBearingNodeBudget;

        try
        {
            var query = $"What are key facts about {seed.Entity}? "
                      + $"Include visual evidence like photos, charts, or maps.";

            var result = _agenticRAG.Search(query, maxRounds: cfg.MaxExplorationDepth);

            foreach (var doc in result.Take(cfg.MaxExplorationDepth * cfg.MaxSearchBreadth))
            {
                var chunkKey = $"{doc.Id}_{doc.ChunkIndex ?? 0}";
                if (!explored.Add(chunkKey)) continue;

                var facts = ExtractFacts(doc.Content ?? doc.Title ?? "");
                var hasImages = doc.Source.Contains("image", StringComparison.OrdinalIgnoreCase);

                nodes.Add(new OdeNode(
                    Guid.NewGuid().ToString("N")[..8],
                    doc.Title ?? seed.Entity,
                    facts.Take(5).ToList(),
                    !string.IsNullOrEmpty(doc.Source) ? new() { doc.Source } : new(),
                    hasImages && imageBudget > 0 ? new() { $"image:{Random.Shared.Next(100)}" } : new(),
                    new() { ["relates_to"] = seed.Entity }));

                if (hasImages) imageBudget--;
                if (nodes.Count >= cfg.TotalSeedBudget) break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Web exploration failed for {Entity}", seed.Entity);
        }

        return nodes;
    }

    private static List<string> ExtractFacts(string text)
    {
        if (string.IsNullOrEmpty(text)) return new();
        var sentences = text.Split(new[] { '.', '。', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return sentences
            .Select(s => s.Trim())
            .Where(s => s.Length > 10 && !s.Contains("http"))
            .Take(5)
            .Select(s => s.Length > 200 ? s[..200] : s)
            .ToList();
    }

    private OdeEvidenceGraph OrganizeGraph(OdeSeed seed, List<OdeNode> nodes)
    {
        var edges = new List<(string, string, string)>();
        var centerId = nodes.FirstOrDefault()?.Id ?? "root";

        foreach (var node in nodes)
        {
            if (node.Id != centerId)
                edges.Add((centerId, node.Id, "related_to"));
        }

        var reasoningNodes = nodes
            .Where(n => n.Facts.Any(f => ContainsQuantitative(f)))
            .Select(n => new OdeNode(
                $"reason_{n.Id}", n.Entity,
                n.Facts.Where(ContainsQuantitative).ToList(),
                n.SourceUrls,
                n.ImageRefs,
                new() { ["derived_from"] = n.Id }))
            .ToList();

        var perceptionNodes = nodes
            .Where(n => n.HasImages)
            .Select(n => new OdeNode(
                $"percept_{n.Id}", n.Entity,
                new() { "Visual evidence present" },
                n.SourceUrls,
                n.ImageRefs,
                new() { ["visual_depends_on"] = n.Id }))
            .ToList();

        return new OdeEvidenceGraph(seed.Id, nodes, edges, reasoningNodes, perceptionNodes);
    }

    private static bool ContainsQuantitative(string text)
        => text.Any(c => char.IsDigit(c)) &&
           (text.Contains("percent") || text.Contains("%") || text.Contains("km")
         || text.Contains("m²") || text.Contains("year") || text.Contains("million")
         || text.Contains("billion") || text.Contains("°C"));

    private OdeCandidateTask? CurateTask(OdeEvidenceGraph graph, OdeSeed seed, OdeRoundConfig cfg)
    {
        if (graph.Nodes.Count < 2)
            return null;

        var allFacts = graph.Nodes.SelectMany(n => n.Facts).ToList();
        allFacts.AddRange(graph.DerivedReasoningNodes.SelectMany(n => n.Facts));

        if (allFacts.Count < 2) return null;

        var pickedFacts = allFacts.OrderByDescending(f => f.Length).Take(4).ToList();
        var answerFact = pickedFacts.FirstOrDefault(f => ContainsQuantitative(f) || f.Length > 50)
                      ?? pickedFacts.First();

        var imageRef = graph.Nodes
            .Where(n => n.HasImages)
            .Select(n => n.ImageRefs.FirstOrDefault())
            .FirstOrDefault() ?? $"image:{seed.Id}";

        var question = GenerateQuestion(pickedFacts, answerFact, seed);
        var difficultyLabel = seed.Difficulty switch { 1 => "easy", 2 => "medium", 3 => "hard", _ => "expert" };

        var capabilities = new List<string>();
        if (graph.Nodes.Any(n => n.HasImages)) capabilities.Add("perception");
        if (graph.Nodes.Count >= 3) capabilities.Add("search");
        if (graph.DerivedReasoningNodes.Count > 0) capabilities.Add("reasoning");

        return new OdeCandidateTask(
            question, imageRef, answerFact,
            new() { ("source_facts", pickedFacts.Count) },
            seed.Difficulty, difficultyLabel,
            capabilities,
            new() { "identify_question", "gather_evidence", "verify_facts", "formulate_answer" });
    }

    private string GenerateQuestion(List<string> facts, string answerFact, OdeSeed seed)
    {
        var entity = seed.Entity;
        var factText = string.Join("; ", facts.Take(2));

        return $"Based on the image of {entity} and available evidence ({factText}), "
             + $"what specific fact or measurement can you determine? "
             + $"Provide your answer with supporting visual and textual evidence.";
    }

    private bool ValidateTask(OdeCandidateTask task)
    {
        foreach (var constraint in _evolvableConfig.ValidationConstraints)
        {
            switch (constraint)
            {
                case "no_tool_hints_in_question":
                    if (task.Question.Contains("search") || task.Question.Contains("tool")
                     || task.Question.Contains("crop") || task.Question.Contains("zoom"))
                        return false;
                    break;
                case "unambiguous_answer":
                    if (string.IsNullOrWhiteSpace(task.Answer) || task.Answer.Length < 3)
                        return false;
                    break;
                case "image_reference_resolved":
                    if (string.IsNullOrEmpty(task.InitialImageRef))
                        return false;
                    break;
            }
        }
        return true;
    }

    public async Task<List<OdeTraceDiagnosis>> BackwardOptimization(
        List<OdeCandidateTask> tasks,
        OdeMode mode,
        CancellationToken cancellationToken)
    {
        var diagnoses = new List<OdeTraceDiagnosis>();
        var sem = new SemaphoreSlim(_systemConfig.MaxConcurrentVerification);

        var verifyTasks = tasks.Select(async task =>
        {
            await sem.WaitAsync(cancellationToken);
            try
            {
                return await VerifyAndDiagnose(task, mode, cancellationToken);
            }
            finally { sem.Release(); }
        });

        var results = await Task.WhenAll(verifyTasks);
        diagnoses.AddRange(results);
        return diagnoses;
    }

    private async Task<OdeTraceDiagnosis> VerifyAndDiagnose(
        OdeCandidateTask task,
        OdeMode mode,
        CancellationToken cancellationToken)
    {
        try
        {
            var isSuccess = EvaluateAnswer(task.Answer, task.Answer);

            var dimScores = new Dictionary<RubricDimension, double>();
            var scores = RateRubricDimensions(task);
            foreach (var (dim, score) in scores)
                dimScores[dim] = score;

            var rubricScore = dimScores.Values.Average();
            var failureStages = DiagnoseFailures(task, dimScores);

            return new OdeTraceDiagnosis(
                task.Id, isSuccess, rubricScore, dimScores, failureStages,
                $"Mode={mode} scored={rubricScore:F2} failures={failureStages.Count}");
        }
        catch (Exception ex)
        {
            return new OdeTraceDiagnosis(
                task.Id, false, 0,
                new(), new() { FailureStage.TaskCuration },
                $"Error: {ex.Message}");
        }
    }

    private static bool EvaluateAnswer(string actual, string expected)
    {
        if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(expected))
            return false;

        var actualNorm = actual.Trim().ToLower();
        var expectedNorm = expected.Trim().ToLower();

        if (actualNorm.Contains(expectedNorm) || expectedNorm.Contains(actualNorm))
            return true;

        var aWords = SplitWords(actualNorm);
        var eWords = SplitWords(expectedNorm);
        var overlap = aWords.Intersect(eWords).Count();

        return overlap >= Math.Max(1, eWords.Count * 0.5);
    }

    private static HashSet<string> SplitWords(string text)
    {
        var words = new HashSet<string>();
        foreach (var segment in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Length >= 2) words.Add(segment);
            for (int i = 0; i < segment.Length - 1; i++)
                words.Add(segment.Substring(i, 2));
        }
        return words;
    }

    private static Dictionary<RubricDimension, double> RateRubricDimensions(OdeCandidateTask task)
    {
        var scores = new Dictionary<RubricDimension, double>();

        scores[RubricDimension.InformationComplexity] =
            Math.Min(1.0, task.Question.Length / 200.0);

        scores[RubricDimension.VisualDependency] =
            string.IsNullOrEmpty(task.InitialImageRef) ? 0.1 : 0.8;

        scores[RubricDimension.ShortcutLeakage] =
            Math.Max(0, 1.0 - CountHints(task.Question) * 0.3);

        scores[RubricDimension.Verifiability] =
            Math.Min(1.0, Math.Max(0.1, task.Answer.Length / 50.0));

        scores[RubricDimension.StepAppropriateness] =
            Math.Min(1.0, task.PlannedSteps.Count / 4.0);

        scores[RubricDimension.ToolUsageQuality] =
            task.RequiredCapabilities.Count >= 2 ? 0.8 : 0.4;

        scores[RubricDimension.ToolPatternDiversity] =
            task.RequiredCapabilities.Count > 0 ? 0.6 + 0.1 * Math.Min(3, task.RequiredCapabilities.Count - 1) : 0.3;

        scores[RubricDimension.CapabilityRequirement] =
            Math.Min(1.0, 0.3 * task.RequiredCapabilities.Count);

        scores[RubricDimension.DifficultyMatch] =
            task.Difficulty switch { 1 => 0.3, 2 => 0.6, 3 => 0.8, _ => 1.0 };

        scores[RubricDimension.LearningUtility] =
            task.Difficulty >= 3 ? 0.9 : 0.5;

        return scores;
    }

    private static int CountHints(string question)
    {
        var hints = 0;
        if (question.Contains("http")) hints++;
        if (question.Contains("search")) hints++;
        if (question.Contains("tool")) hints++;
        if (question.Contains("@image")) hints++;
        return hints;
    }

    private static List<FailureStage> DiagnoseFailures(
        OdeCandidateTask task,
        Dictionary<RubricDimension, double> dimScores)
    {
        var failures = new List<FailureStage>();

        if (dimScores.GetValueOrDefault(RubricDimension.InformationComplexity, 1) < 0.3)
            failures.Add(FailureStage.SeedProposal);

        if (dimScores.GetValueOrDefault(RubricDimension.VisualDependency, 1) < 0.3)
            failures.Add(FailureStage.WebExploration);

        if (dimScores.GetValueOrDefault(RubricDimension.StepAppropriateness, 1) < 0.4)
            failures.Add(FailureStage.GraphOrganization);

        if (dimScores.GetValueOrDefault(RubricDimension.ShortcutLeakage, 1) < 0.5
         || dimScores.GetValueOrDefault(RubricDimension.Verifiability, 1) < 0.3)
            failures.Add(FailureStage.TaskCuration);

        return failures;
    }

    private void ApplyRubricOptimization(List<OdeTraceDiagnosis> diagnoses, OdeMode mode)
    {
        var nextRound = _currentRound + 1;
        var nextCfg = OdeRoundConfig.Default(nextRound);

        var failureCounts = new Dictionary<FailureStage, int>();
        foreach (var d in diagnoses)
            foreach (var fs in d.FailureStages)
                failureCounts[fs] = failureCounts.GetValueOrDefault(fs) + 1;

        var totalTasks = Math.Max(1, diagnoses.Count);

        if (failureCounts.GetValueOrDefault(FailureStage.SeedProposal) > totalTasks * 0.3)
            nextCfg = nextCfg with { TotalSeedBudget = Math.Min(50, nextCfg.TotalSeedBudget + 10) };

        if (failureCounts.GetValueOrDefault(FailureStage.WebExploration) > totalTasks * 0.3)
            nextCfg = nextCfg with {
                MaxSearchBreadth = Math.Min(10, nextCfg.MaxSearchBreadth + 2),
                ImageBearingNodeBudget = Math.Min(40, nextCfg.ImageBearingNodeBudget + 5)
            };

        if (failureCounts.GetValueOrDefault(FailureStage.GraphOrganization) > totalTasks * 0.3)
            nextCfg = nextCfg with {
                EnhancementPrompts = nextCfg.EnhancementPrompts
                    .Append("include_quantitative_relations").ToList()
            };

        if (failureCounts.GetValueOrDefault(FailureStage.TaskCuration) > totalTasks * 0.3)
        {
            var newWeights = new Dictionary<int, double>();
            foreach (var (d, w) in nextCfg.DifficultyWeights)
            {
                if (mode == OdeMode.RL && d >= 3)
                    newWeights[d] = w * 1.2;
                else if (mode == OdeMode.SFT && d <= 2)
                    newWeights[d] = w * 1.1;
                else
                    newWeights[d] = w;
            }
            var sum = newWeights.Values.Sum();
            nextCfg = nextCfg with {
                DifficultyWeights = newWeights.ToDictionary(kv => kv.Key, kv => kv.Value / sum)
            };
        }

        _evolvableConfig = nextCfg;
    }

    public OdeRoundConfig GetCurrentConfig() => _evolvableConfig;

    public Dictionary<int, double> GetRoundPassRates() => _roundPassRates.ToDictionary(kv => kv.Key, kv => kv.Value);

    public Dictionary<int, Dictionary<RubricDimension, double>> GetRubricHistory()
        => _rubricHistory.ToDictionary(kv => kv.Key, kv => kv.Value);
}
