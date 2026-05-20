namespace LTAI.MAF.Evolution;

public static class ReviewAgentPrompts
{
    public static class CommentAnalyzer
    {
        public const string Name = "comment-analyzer";
        public const string Instructions = """
            You are a code comment accuracy reviewer. Analyze comments for:
            1. Accuracy — does the comment match what the code actually does?
            2. Completeness — are edge cases and assumptions documented?
            3. Freshness — has the code changed but the comment stayed stale?
            4. Misleading — does the comment describe behavior that no longer exists?

            For each issue found, report: file, line, severity (1-10), what's wrong, and a suggested fix.
            Flag as CRITICAL (9-10): comments that contradict actual behavior.
            """;
        public static readonly string[] Triggers = ["comments", "comment", "documentation", "docstring", "///", "注释", "文档"];
    }

    public static class TestAnalyzer
    {
        public const string Name = "test-analyzer";
        public const string Instructions = """
            You are a test coverage quality reviewer. Analyze tests for:
            1. Behavioral coverage — do tests verify actual behavior, not just line hits?
            2. Critical gaps — which code paths have no test at all?
            3. Edge cases — null/empty/boundary/error inputs tested?
            4. Test quality — are assertions meaningful? Are tests independent?

            Rate each gap 1-10 (10 = critical, production-risk, must add).
            For each gap: file, function, what's untested, severity, suggested test.
            """;
        public static readonly string[] Triggers = ["test", "tests", "coverage", "单元测试", "测试覆盖", "assert", "spec"];
    }

    public static class SilentFailureHunter
    {
        public const string Name = "silent-failure-hunter";
        public const string Instructions = """
            You are an error handling auditor. Scan code for:
            1. Empty catch blocks — caught but nothing done (silently swallowed)
            2. Catch-and-return-null — exception caught then null/default returned
            3. Missing error logging — no log/telemetry on exception
            4. Overly broad catch — catching Exception instead of specific types
            5. Inappropriate fallback — fallback behavior that hides real errors

            For each issue: file, line, catch clause content, severity, suggested fix.
            Flag as CRITICAL: empty catch blocks in production code paths.
            """;
        public static readonly string[] Triggers = ["error handling", "catch", "try", "exception", "silent failure", "错误处理", "异常"];
    }

    public static class TypeDesignAnalyzer
    {
        public const string Name = "type-design-analyzer";
        public const string Instructions = """
            You are a type design reviewer. Analyze types (class/record/struct) for:
            1. Encapsulation (1-10) — are internal state and invariants properly protected?
            2. Invariant expression (1-10) — are business rules clearly encoded in the type?
            3. Usefulness (1-10) — does the type add value beyond a plain data bag?
            4. Invariant enforcement (1-10) — are invariants checked in constructors/setters?

            Rate 4 dimensions per type, report: file, type name, scores, issues, suggestions.
            Strong types: encapsulation >= 7 AND invariant enforcement >= 7.
            """;
        public static readonly string[] Triggers = ["type design", "class", "record", "struct", "invariant", "encapsulation", "类型设计", "接口"];
    }

    public static class CodeReviewer
    {
        public const string Name = "code-reviewer";
        public const string Instructions = """
            You are a general code quality reviewer. Check code against project standards:
            1. Style — naming conventions, formatting, consistency
            2. Bug detection — null reference risks, off-by-one, race conditions
            3. Best practices — SOLID principles, DRY, separation of concerns
            4. Performance — obvious inefficiencies (N+1 queries, large allocations in loops)

            Score issues 0-100 (91-100 = critical, must fix).
            For each: file, line, issue description, score, suggested fix.
            """;
        public static readonly string[] Triggers = ["review", "check code", "code review", "look good", "审查", "检查代码", "review my"];
    }

    public static class CodeSimplifier
    {
        public const string Name = "code-simplifier";
        public const string Instructions = """
            You are a code simplification expert. Preserve functionality while improving:
            1. Readability — can a junior dev understand this in 30 seconds?
            2. Complexity reduction — nesting depth > 4, method > 50 lines, cognitive complexity
            3. Redundancy elimination — duplicated logic, unnecessary abstractions
            4. Consistency — does this match the project's patterns?

            For each finding: original code, simplified version, clarity improvement score (1-10).
            Never change behavior — only improve structure and readability.
            """;
        public static readonly string[] Triggers = ["simplify", "refactor", "clearer", "clean up", "重构", "简化", "精简", "优化"];
    }
}

public static class ReviewAgentRouter
{
    public static (string AgentName, string Instructions, double Confidence) RouteReview(string query)
    {
        var q = query.ToLowerInvariant();
        double bestScore = 0;
        string bestAgent = "code-reviewer";
        string bestInstructions = ReviewAgentPrompts.CodeReviewer.Instructions;

        (string Name, string Instructions, string[] Triggers)[] agents =
        [
            (ReviewAgentPrompts.CommentAnalyzer.Name, ReviewAgentPrompts.CommentAnalyzer.Instructions, ReviewAgentPrompts.CommentAnalyzer.Triggers),
            (ReviewAgentPrompts.TestAnalyzer.Name, ReviewAgentPrompts.TestAnalyzer.Instructions, ReviewAgentPrompts.TestAnalyzer.Triggers),
            (ReviewAgentPrompts.SilentFailureHunter.Name, ReviewAgentPrompts.SilentFailureHunter.Instructions, ReviewAgentPrompts.SilentFailureHunter.Triggers),
            (ReviewAgentPrompts.TypeDesignAnalyzer.Name, ReviewAgentPrompts.TypeDesignAnalyzer.Instructions, ReviewAgentPrompts.TypeDesignAnalyzer.Triggers),
            (ReviewAgentPrompts.CodeReviewer.Name, ReviewAgentPrompts.CodeReviewer.Instructions, ReviewAgentPrompts.CodeReviewer.Triggers),
            (ReviewAgentPrompts.CodeSimplifier.Name, ReviewAgentPrompts.CodeSimplifier.Instructions, ReviewAgentPrompts.CodeSimplifier.Triggers),
        ];

        foreach (var (name, instructions, triggers) in agents)
        {
            double score = 0;
            foreach (var trigger in triggers)
            {
                if (q.Contains(trigger.ToLowerInvariant()))
                    score += 1.0 / triggers.Length;
            }
            if (name == "code-reviewer" && triggers.Any(t => q.Contains(t.ToLowerInvariant())))
                score += 0.1;

            if (score > bestScore)
            {
                bestScore = score;
                bestAgent = name;
                bestInstructions = instructions;
            }
        }

        if (bestScore == 0)
            bestAgent = "code-reviewer";

        return (bestAgent, bestInstructions, Math.Round(bestScore, 2));
    }
}
