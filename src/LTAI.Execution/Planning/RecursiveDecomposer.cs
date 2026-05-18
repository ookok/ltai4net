using System.Text.RegularExpressions;
using LTAI.Execution.Models;

namespace LTAI.Execution.Planning;

public record SubTaskR(
    string Id,
    string Description,
    string TaskType,
    List<string> Dependencies,
    DepType DepType,
    string Agent,
    int EstimatedTokens,
    string Result,
    string Status)
{
    public SubTaskR MarkCompleted(string result) => this with { Status = "completed", Result = result };

    public SubTaskR MarkFailed(string error) => this with { Status = "failed", Result = error };

    public SubTaskR MarkRunning() => this with { Status = "running" };

    public bool IsReady(HashSet<string> completedIds) =>
        Status == "pending" && Dependencies.All(d => completedIds.Contains(d));
}

public class RecursiveDecomposer
{
    private static RecursiveDecomposer? _instance;
    private static readonly Lock InstanceLock = new();

    public static readonly Dictionary<string, List<(string label, string role, DepType depType)>>
        DECOMPOSITION_PATTERNS = new()
        {
            ["analyze"] = new()
            {
                ("gather_data", "general", DepType.SEQUENTIAL),
                ("perform_analysis", "analyst", DepType.SEQUENTIAL),
                ("summarize_findings", "general", DepType.SEQUENTIAL)
            },
            ["implement"] = new()
            {
                ("design_solution", "architect", DepType.SEQUENTIAL),
                ("write_code", "coder", DepType.SEQUENTIAL),
                ("test_implementation", "tester", DepType.SEQUENTIAL),
                ("document_changes", "general", DepType.SEQUENTIAL)
            },
            ["compare"] = new()
            {
                ("extract_features", "general", DepType.SEQUENTIAL),
                ("perform_comparison", "analyst", DepType.SEQUENTIAL),
                ("present_results", "general", DepType.SEQUENTIAL)
            },
            ["search"] = new()
            {
                ("formulate_query", "general", DepType.SEQUENTIAL),
                ("execute_search", "researcher", DepType.SEQUENTIAL),
                ("evaluate_results", "general", DepType.SEQUENTIAL)
            },
            ["summarize"] = new()
            {
                ("extract_key_points", "general", DepType.SEQUENTIAL),
                ("organize_content", "general", DepType.SEQUENTIAL),
                ("write_summary", "writer", DepType.SEQUENTIAL)
            }
        };

    private RecursiveDecomposer() { }

    public static RecursiveDecomposer GetRecursiveDecomposer()
    {
        if (_instance is null)
        {
            lock (InstanceLock)
            {
                _instance ??= new RecursiveDecomposer();
            }
        }
        return _instance;
    }

    public List<SubTaskR> Decompose(string task, int maxDepth = 3)
    {
        if (IsAtomic(task) || maxDepth <= 0)
            return Atomize(task);

        var subtasks = Plan(task, task.ToLowerInvariant());

        if (subtasks.Count == 0)
        {
            subtasks = DefaultDecompose(task);
        }

        return subtasks;
    }

    public List<List<SubTaskR>> ParallelGroups(List<SubTaskR> subtasks)
    {
        var groups = new List<List<SubTaskR>>();
        var remaining = subtasks.ToList();
        var completed = new HashSet<string>();
        var visited = new HashSet<string>();

        while (remaining.Count > 0)
        {
            var currentGroup = new List<SubTaskR>();
            var toRemove = new List<SubTaskR>();

            foreach (var st in remaining)
            {
                if (st.Dependencies.All(d => completed.Contains(d)))
                {
                    currentGroup.Add(st);
                    toRemove.Add(st);
                }
            }

            if (currentGroup.Count == 0)
            {
                var deadlocked = remaining.Select(r => r.Id).ToList();
                throw new InvalidOperationException(
                    $"Deadlock detected: unresolved dependencies [{string.Join(", ", deadlocked)}]");
            }

            groups.Add(currentGroup);

            foreach (var st in toRemove)
            {
                remaining.Remove(st);
                completed.Add(st.Id);
            }
        }

        return groups;
    }

    public bool IsAtomic(string task)
    {
        if (string.IsNullOrWhiteSpace(task))
            return true;

        var words = task.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length < 5;
    }

    public List<SubTaskR> Plan(string task, string taskLower)
    {
        var matched = MatchPattern(taskLower);
        if (matched.Count == 0)
            return new();

        return BuildDependencyChain(matched, task);
    }

    private List<SubTaskR> Atomize(string task)
    {
        return new()
        {
            new SubTaskR(
                Id: Guid.NewGuid().ToString("N")[..12],
                Description: task,
                TaskType: "ATOMIC",
                Dependencies: new(),
                DepType: DepType.SEQUENTIAL,
                Agent: "general",
                EstimatedTokens: EstimateTokens(task),
                Result: "",
                Status: "pending")
        };
    }

    private List<(string label, string role, DepType depType)> MatchPattern(string taskLower)
    {
        foreach (var (keyword, pattern) in DECOMPOSITION_PATTERNS)
        {
            if (taskLower.Contains(keyword))
                return pattern;
        }

        return new();
    }

    private List<SubTaskR> BuildDependencyChain(
        List<(string label, string role, DepType depType)> pattern,
        string task)
    {
        var subtasks = new List<SubTaskR>();
        var prevId = "";

        for (var i = 0; i < pattern.Count; i++)
        {
            var (label, role, depType) = pattern[i];
            var id = Guid.NewGuid().ToString("N")[..12];
            var deps = i > 0 ? new List<string> { prevId } : new List<string>();

            var estTokens = role switch
            {
                "analyst" => 2000,
                "architect" => 3000,
                "coder" => 4000,
                "researcher" => 2500,
                "writer" => 1500,
                "tester" => 2000,
                _ => 1000
            };

            subtasks.Add(new SubTaskR(
                Id: id,
                Description: $"{label}: {task}",
                TaskType: "COMPOSITE",
                Dependencies: deps,
                DepType: depType,
                Agent: role,
                EstimatedTokens: estTokens,
                Result: "",
                Status: "pending"));

            prevId = id;
        }

        return subtasks;
    }

    private List<SubTaskR> DefaultDecompose(string task)
    {
        var subtasks = new List<SubTaskR>();
        var ids = new List<string>();

        var steps = new[] { "understand", "execute", "verify" };
        var roles = new[] { "general", "general", "general" };
        var tokens = new[] { 1000, 2000, 1000 };

        for (var i = 0; i < steps.Length; i++)
        {
            var id = Guid.NewGuid().ToString("N")[..12];
            var deps = i > 0 ? new List<string> { ids[^1] } : new List<string>();

            ids.Add(id);

            subtasks.Add(new SubTaskR(
                Id: id,
                Description: $"{steps[i]}: {task}",
                TaskType: "COMPOSITE",
                Dependencies: deps,
                DepType: DepType.SEQUENTIAL,
                Agent: roles[i],
                EstimatedTokens: tokens[i],
                Result: "",
                Status: "pending"));
        }

        return subtasks;
    }

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var wordChars = text.Count(char.IsLetterOrDigit);
        var nonWordChars = text.Length - wordChars;

        return (int)(wordChars / 3.5 + nonWordChars / 2.0);
    }
}
