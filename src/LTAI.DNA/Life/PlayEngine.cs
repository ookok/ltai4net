using LTAI.DNA.Models;

namespace LTAI.DNA.Life;

public sealed class PlayEngine
{
    private readonly List<PlayOutcome> _history = new();
    private int _sessionsCompleted;
    private readonly object _lock = new();

    private static readonly List<(string name, HeadRole role, Dictionary<string, double> traits)> DefaultHeads = new()
    {
        ("Ananta", HeadRole.ResearchAid, new() { ["curiosity"] = 0.9, ["creativity"] = 0.7 }),
        ("Vasuki", HeadRole.CodeAssistant, new() { ["precision"] = 0.85, ["persistence"] = 0.75 }),
        ("Shesha", HeadRole.Planner, new() { ["creativity"] = 0.8, ["openness"] = 0.7 }),
        ("Garuda", HeadRole.Explorer, new() { ["curiosity"] = 0.95, ["openness"] = 0.85 }),
        ("Nandi", HeadRole.Teacher, new() { ["empathy"] = 0.85, ["persistence"] = 0.7 }),
        ("Matsya", HeadRole.Critic, new() { ["precision"] = 0.9, ["caution"] = 0.75 }),
        ("Kurma", HeadRole.OpsAgent, new() { ["precision"] = 0.8, ["caution"] = 0.8 }),
        ("Simha", HeadRole.SocialAgent, new() { ["empathy"] = 0.9, ["openness"] = 0.75 })
    };

    private static readonly string[] CodeProblems =
    {
        "Implement a thread-safe LRU cache with generics in C#",
        "Design a rate limiter using the sliding window algorithm",
        "Refactor this monolith method into clean architecture patterns",
        "Write a recursive descent parser for arithmetic expressions"
    };

    private static readonly string[] DebateTopics =
    {
        "Static typing vs dynamic typing for AI systems",
        "Microservices vs monolith for AI agent platforms",
        "Is AGI possible through scaling alone?",
        "Open source vs proprietary AI models"
    };

    private static readonly string[] PlanningTasks =
    {
        "Plan a multi-agent system for automated code review",
        "Design a distributed knowledge retrieval pipeline",
        "Create a self-evolving AI training curriculum",
        "Architect a real-time collaborative coding environment"
    };

    private static readonly string[] CrisisScenarios =
    {
        "A critical security vulnerability is discovered in the deployed system",
        "The primary model provider is down, users waiting",
        "A feedback loop is causing model outputs to degrade",
        "Resource exhaustion: 1000 concurrent tasks overwhelming the system"
    };

    public async Task<PlayOutcome> RunScenario(PlayScenario scenario, List<string> headIds, Func<string, Task<string>> chat)
    {
        var outcome = scenario switch
        {
            PlayScenario.CodeReview => await RunCodeReview(headIds, chat),
            PlayScenario.Debate => await RunDebate(headIds, chat),
            PlayScenario.CoPlanning => await RunCoPlanning(headIds, chat),
            PlayScenario.Negotiation => await RunNegotiation(headIds, chat),
            PlayScenario.Critique => await RunCritique(headIds, chat),
            PlayScenario.Teaching => await RunTeaching(headIds, chat),
            PlayScenario.Puzzle => await RunPuzzle(headIds, chat),
            PlayScenario.Crisis => await RunCrisis(headIds, chat),
            _ => new PlayOutcome { Scenario = scenario, Resolution = "Unknown scenario" }
        };

        lock (_lock)
        {
            _history.Add(outcome);
            _sessionsCompleted++;
            if (_history.Count > 500) _history.RemoveAt(0);
        }

        return outcome;
    }

    private async Task<PlayOutcome> RunCodeReview(List<string> heads, Func<string, Task<string>> chat)
    {
        if (heads.Count < 2) heads.AddRange(DefaultHeads.Take(2).Select(h => h.name));
        var problem = CodeProblems[Random.Shared.Next(CodeProblems.Length)];
        var turns = new List<PlayTurn>();

        var authorResult = await chat($"As a developer, write code for: {problem}");
        turns.Add(new PlayTurn { TurnNumber = 1, FromHead = heads[0], Action = authorResult ?? "" });

        var reviewResult =
            await chat(
                $"As a code reviewer, review this code and provide constructive feedback:\n{authorResult}");
        turns.Add(new PlayTurn { TurnNumber = 2, FromHead = heads[1], Action = reviewResult ?? "" });

        var revisionResult =
            await chat(
                $"As an author, incorporate this feedback to improve the code:\nFeedback: {reviewResult}\n\nOriginal:\n{authorResult}");
        turns.Add(new PlayTurn { TurnNumber = 3, FromHead = heads[0], Action = revisionResult ?? "" });

        var judgeResult =
            await chat(
                $"As a judge, evaluate the quality of the collaboration. Score cooperation (0-1) and list learning points:\n{string.Join("\n", turns.Select(t => $"[Turn {t.TurnNumber}] {t.FromHead}: {t.Action}"))}");

        return new PlayOutcome
        {
            Scenario = PlayScenario.CodeReview,
            Participants = heads.Take(2).ToList(),
            Turns = turns,
            Resolution = judgeResult ?? "Collaboration completed",
            CooperationScore = 0.7,
            LearningPoints = new List<string> { "Code quality improved through review", "Constructive feedback patterns" },
            DurationMs = 100
        };
    }

    private async Task<PlayOutcome> RunDebate(List<string> heads, Func<string, Task<string>> chat)
    {
        if (heads.Count < 2) heads.AddRange(DefaultHeads.Take(2).Select(h => h.name));
        var topic = DebateTopics[Random.Shared.Next(DebateTopics.Length)];
        var turns = new List<PlayTurn>();

        var proResult = await chat($"Argue FOR: {topic}");
        turns.Add(new PlayTurn { TurnNumber = 1, FromHead = heads[0], Action = proResult ?? "" });

        var conResult = await chat($"Argue AGAINST this position:\n{proResult}");
        turns.Add(new PlayTurn { TurnNumber = 2, FromHead = heads[1], Action = conResult ?? "" });

        var rebuttal =
            await chat($"Rebuttal to this counter-argument:\n{conResult}\n\nYour original position:\n{proResult}");
        turns.Add(new PlayTurn { TurnNumber = 3, FromHead = heads[0], Action = rebuttal ?? "" });

        return new PlayOutcome
        {
            Scenario = PlayScenario.Debate,
            Participants = heads.Take(2).ToList(),
            Turns = turns,
            Resolution = $"Debate concluded on: {topic}",
            CooperationScore = 0.6,
            LearningPoints = new List<string> { "Topic: " + topic, "Multi-perspective reasoning" },
            DurationMs = 100
        };
    }

    private async Task<PlayOutcome> RunCoPlanning(List<string> heads, Func<string, Task<string>> chat)
    {
        if (heads.Count < 2) heads.AddRange(DefaultHeads.Take(3).Select(h => h.name));
        var task = PlanningTasks[Random.Shared.Next(PlanningTasks.Length)];
        var turns = new List<PlayTurn>();

        var visionResult = await chat($"As a visionary planner, outline a bold vision for: {task}");
        turns.Add(new PlayTurn { TurnNumber = 1, FromHead = heads[0], Action = visionResult ?? "" });

        var breakdownResult =
            await chat($"Break this vision into concrete actionable steps:\n{visionResult}");
        turns.Add(new PlayTurn { TurnNumber = 2, FromHead = heads[1], Action = breakdownResult ?? "" });

        var validateResult =
            await chat(
                $"Validate this plan for feasibility, risks, and dependencies:\n{breakdownResult}");
        turns.Add(new PlayTurn { TurnNumber = 3, FromHead = heads.Count > 2 ? heads[2] : heads[1], Action = validateResult ?? "" });

        return new PlayOutcome
        {
            Scenario = PlayScenario.CoPlanning,
            Participants = heads.Take(Math.Min(3, heads.Count)).ToList(),
            Turns = turns,
            Resolution = $"Co-planning completed for: {task}",
            CooperationScore = 0.75,
            LearningPoints = new List<string> { "Vision→Breakdown→Validate workflow", "Task: " + task },
            DurationMs = 100
        };
    }

    private async Task<PlayOutcome> RunNegotiation(List<string> heads, Func<string, Task<string>> chat)
    {
        if (heads.Count < 2) heads.AddRange(DefaultHeads.Take(2).Select(h => h.name));
        var turns = new List<PlayTurn>();

        var proposalResult =
            await chat($"Propose a resource allocation plan for {heads[0]} and {heads[1]} sharing compute resources.");
        turns.Add(new PlayTurn { TurnNumber = 1, FromHead = heads[0], Action = proposalResult ?? "" });

        var counterResult = await chat($"Counter-proposal to this plan:\n{proposalResult}");
        turns.Add(new PlayTurn { TurnNumber = 2, FromHead = heads[1], Action = counterResult ?? "" });

        var compromiseResult =
            await chat(
                $"Find a compromise between these two proposals:\nA: {proposalResult}\nB: {counterResult}");
        turns.Add(new PlayTurn { TurnNumber = 3, FromHead = heads[0], Action = compromiseResult ?? "" });

        return new PlayOutcome
        {
            Scenario = PlayScenario.Negotiation,
            Participants = heads.Take(2).ToList(),
            Turns = turns,
            Resolution = compromiseResult ?? "Compromise reached",
            CooperationScore = 0.65,
            LearningPoints = new List<string> { "Resource negotiation", "Compromise strategies" },
            DurationMs = 100
        };
    }

    private async Task<PlayOutcome> RunCritique(List<string> heads, Func<string, Task<string>> chat)
    {
        if (heads.Count < 2) heads.AddRange(DefaultHeads.Take(2).Select(h => h.name));
        var turns = new List<PlayTurn>();

        var workResult = await chat("Create a system design for a real-time AI agent dashboard.");
        turns.Add(new PlayTurn { TurnNumber = 1, FromHead = heads[0], Action = workResult ?? "" });

        var critiqueResult = await chat(
            $"Provide critical analysis of this design, identifying weaknesses and improvements:\n{workResult}");
        turns.Add(new PlayTurn { TurnNumber = 2, FromHead = heads[1], Action = critiqueResult ?? "" });

        return new PlayOutcome
        {
            Scenario = PlayScenario.Critique,
            Participants = heads.Take(2).ToList(),
            Turns = turns,
            Resolution = $"Critique session completed",
            CooperationScore = 0.55,
            LearningPoints = new List<string> { "Constructive criticism", "Design improvement" },
            DurationMs = 100
        };
    }

    private async Task<PlayOutcome> RunTeaching(List<string> heads, Func<string, Task<string>> chat)
    {
        if (heads.Count < 2) heads.AddRange(DefaultHeads.Take(2).Select(h => h.name));
        var turns = new List<PlayTurn>();

        var topic = "Polly resilience library in .NET: circuit breaker and retry patterns";
        var teachResult = await chat($"As a teacher, explain {topic} to a student.");
        turns.Add(new PlayTurn { TurnNumber = 1, FromHead = heads[0], Action = teachResult ?? "" });

        var questionResult = await chat(
            $"As a student, ask clarifying questions about this explanation:\n{teachResult}");
        turns.Add(new PlayTurn { TurnNumber = 2, FromHead = heads[1], Action = questionResult ?? "" });

        var adjustResult = await chat($"Address these questions and adjust the explanation:\n{questionResult}");
        turns.Add(new PlayTurn { TurnNumber = 3, FromHead = heads[0], Action = adjustResult ?? "" });

        return new PlayOutcome
        {
            Scenario = PlayScenario.Teaching,
            Participants = heads.Take(2).ToList(),
            Turns = turns,
            Resolution = $"Teaching session on {topic} completed",
            CooperationScore = 0.8,
            LearningPoints = new List<string> { "Explaining " + topic, "Interactive teaching patterns" },
            DurationMs = 100
        };
    }

    private async Task<PlayOutcome> RunPuzzle(List<string> heads, Func<string, Task<string>> chat)
    {
        if (heads.Count < 2) heads.AddRange(DefaultHeads.Take(3).Select(h => h.name));
        var turns = new List<PlayTurn>();

        var puzzle = "Design a system where 5 AI agents must coordinate to solve a maze without direct communication, using only environmental markers.";
        var introduceResult = await chat($"Introduce this puzzle: {puzzle}");
        turns.Add(new PlayTurn { TurnNumber = 1, FromHead = heads[0], Action = introduceResult ?? "" });

        for (int i = 1; i < Math.Min(heads.Count, 4); i++)
        {
            var perspectiveResult =
                await chat(
                    $"As {heads[i]}, contribute your perspective on this puzzle: {puzzle}\n\nPrevious contributions: {string.Join(" | ", turns.Select(t => $"[{t.FromHead}]: {t.Action}"))}");
            turns.Add(new PlayTurn { TurnNumber = i + 1, FromHead = heads[i], Action = perspectiveResult ?? "" });
        }

        return new PlayOutcome
        {
            Scenario = PlayScenario.Puzzle,
            Participants = heads.Take(Math.Min(4, heads.Count)).ToList(),
            Turns = turns,
            Resolution = $"Puzzle explored by {turns.Count} participants",
            CooperationScore = 0.7,
            LearningPoints = new List<string> { "Coordination without direct communication", "Environmental markers" },
            DurationMs = 100
        };
    }

    private async Task<PlayOutcome> RunCrisis(List<string> heads, Func<string, Task<string>> chat)
    {
        if (heads.Count < 2) heads.AddRange(DefaultHeads.Take(3).Select(h => h.name));
        var scenario = CrisisScenarios[Random.Shared.Next(CrisisScenarios.Length)];
        var turns = new List<PlayTurn>();

        var directiveResult =
            await chat(
                $"CRISIS: {scenario}\nAs commander, issue directives to team members {string.Join(", ", heads.Skip(1))}.");
        turns.Add(new PlayTurn { TurnNumber = 1, FromHead = heads[0], Action = directiveResult ?? "" });

        for (int i = 1; i < Math.Min(heads.Count, 4); i++)
        {
            var responseResult = await chat(
                $"CRISIS: {scenario}\nCommander's orders: {directiveResult}\nYou are {heads[i]}. Report your actions and status.");
            turns.Add(new PlayTurn { TurnNumber = i + 1, FromHead = heads[i], Action = responseResult ?? "" });
        }

        return new PlayOutcome
        {
            Scenario = PlayScenario.Crisis,
            Participants = heads.Take(Math.Min(4, heads.Count)).ToList(),
            Turns = turns,
            Resolution = $"Crisis response completed for: {scenario}",
            CooperationScore = 0.6,
            LearningPoints = new List<string> { "Crisis management: " + scenario, "Team coordination under pressure" },
            DurationMs = 100
        };
    }

    public async Task<List<PlayOutcome>> AutoPlay(int rounds = 3, Func<string, Task<string>>? chat = null)
    {
        var results = new List<PlayOutcome>();
        var scenarios = Enum.GetValues<PlayScenario>();
        var dummyChat = new Func<string, Task<string>>(msg =>
            Task.FromResult($"Simulated response to: {msg[..Math.Min(msg.Length, 100)]}..."));

        for (int i = 0; i < rounds; i++)
        {
            var scenario = scenarios[Random.Shared.Next(scenarios.Length)];
            var heads = DefaultHeads.OrderBy(_ => Random.Shared.Next()).Take(3)
                .Select(h => h.name).ToList();
            var outcome = await RunScenario(scenario, heads, chat ?? dummyChat);
            results.Add(outcome);
        }

        return results;
    }

    public Dictionary<string, object> Stats()
    {
        lock (_lock)
        {
            return new Dictionary<string, object>
            {
                ["sessions_completed"] = _sessionsCompleted,
                ["total_history"] = _history.Count,
                ["avg_cooperation"] = _history.Count > 0
                    ? _history.Average(o => o.CooperationScore)
                    : 0,
                ["by_scenario"] = _history.GroupBy(o => o.Scenario)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count())
            };
        }
    }
}
