using LTAI.Agent.Vector;

namespace LTAI.Agent.SeedER;

/// <summary>
/// GoS-inspired Belief State Machine: controls when to drill-down (explore deeper)
/// or backtrack (retreat to a higher level) during structured KG exploration.
///
/// Thresholds (from GoS paper):
///   gap_delta   — min confidence gap between top-1 and top-2 for advancement (def: 0.3)
///   min_support — min supporting evidence edges needed (def: 3)
///   max_steps   — max steps allowed at one level before forced advance (def: 2)
/// </summary>
public sealed class BeliefFSM
{
    public int State { get; private set; }          // current depth level
    public string? StateLabel { get; set; } // "active" | "backtrack" | "report"
    public int TotalSteps { get; private set; }

    public double GapDelta { get; }
    public int MinSupport { get; }
    public int MaxSteps { get; }

    private readonly Dictionary<int, int> _stepsAtLevel = [];
    private readonly List<int> _history = [];

    public BeliefFSM(double gapDelta = 0.3, int minSupport = 3, int maxSteps = 2)
    {
        GapDelta = gapDelta;
        MinSupport = minSupport;
        MaxSteps = maxSteps;
        State = 1;
        StateLabel = "active";
    }

    public IReadOnlyList<int> History => _history;

    public void SetState(int level)
    {
        if (level < 1) level = 1;
        _history.Add(State);
        State = level;
        StateLabel = "active";
    }

    public void TickStep()
    {
        TotalSteps++;
        _stepsAtLevel.TryGetValue(State, out var cur);
        _stepsAtLevel[State] = cur + 1;
    }

    /// <summary>
    /// Should we advance to next level? Follows GoS FSM logic:
    /// 1. top-1 confidence gap ≥ gap_delta AND ≥ min_support support edges → advance
    /// 2. steps at this level ≥ max_steps → force advance
    /// 3. otherwise → stay
    /// </summary>
    public bool MaybeAdvance(IReadOnlyList<ExplorationPath> paths)
    {
        var frontier = ExtractFrontier(paths);
        if (frontier == null)
        {
            StateLabel = "report";
            return true; // no viable hypotheses → report
        }

        var gap = ComputeGap(frontier, paths);
        var supportCount = CountSupportEdges(frontier);
        var stepsHere = _stepsAtLevel.GetValueOrDefault(State);

        if (gap >= GapDelta && supportCount >= MinSupport)
            return true;

        if (stepsHere >= MaxSteps)
            return true;

        return false;
    }

    /// <summary>
    /// GoS-style backtracking: check if each ancestor in the refines chain
    /// is still the top-ranked path at its depth level. If not, prune.
    /// Returns the level to backtrack to, or null if no backtrack needed.
    /// </summary>
    public int? CheckBacktrack(ExplorationPath currentPath, IReadOnlyList<ExplorationPath> allPaths)
    {
        // Walk up the path's step chain, checking rank at each depth
        for (int depth = 1; depth < currentPath.Length; depth++)
        {
            var stepNode = currentPath.Steps[depth].Node;
            var stepRank = RankAtDepth(stepNode.Id, depth, allPaths);

            // If this ancestor is not top-1 at its depth, backtrack
            if (stepRank > 0)
            {
                StateLabel = "backtrack";
                return depth; // retreat to this level
            }
        }

        return null;
    }

    /// <summary>Highest-confidence path at current state level.</summary>
    public ExplorationPath? ExtractFrontier(IReadOnlyList<ExplorationPath> paths)
    {
        return paths.Where(p => p.Length >= State)
            .OrderByDescending(p => p.Score)
            .FirstOrDefault();
    }

    private double ComputeGap(ExplorationPath frontier, IReadOnlyList<ExplorationPath> paths)
    {
        var sameLevel = paths.Where(p => p.Length >= State && p != frontier)
            .OrderByDescending(p => p.Score).ToList();

        if (sameLevel.Count == 0) return frontier.Score;
        return frontier.Score - sameLevel[0].Score;
    }

    private int CountSupportEdges(ExplorationPath path)
    {
        return path.Steps.Count(s => s.IncomingEdge?.Relation is "supports" or "contains");
    }

    private int RankAtDepth(long nodeId, int depth, IReadOnlyList<ExplorationPath> allPaths)
    {
        // Count paths at the same depth with a higher score
        var myPath = allPaths.FirstOrDefault(p => p.Length > depth && p.Steps[depth].Node.Id == nodeId);
        if (myPath == null) return int.MaxValue;

        return allPaths.Count(p => p.Length > depth
            && p != myPath
            && p.Score > myPath.Score);
    }

    public override string ToString()
    {
        return $"FSM[level={State}, label={StateLabel}, steps={TotalSteps}, " +
               $"gap_delta={GapDelta}, min_support={MinSupport}, max_steps={MaxSteps}]";
    }
}
