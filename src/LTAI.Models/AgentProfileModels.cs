namespace LTAI.Models;

public sealed class AgentProfile
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string SystemPrompt { get; init; } = "";
    public List<ProfileRule> PermissionRules { get; init; } = [];
    public string? ModelId { get; init; }
    public string? ModelProvider { get; init; }
    public bool IsActive { get; set; }

    public static AgentProfile CreatePlan() => new()
    {
        Name = "plan",
        Description = "Planning agent: reads, analyzes, proposes — no destructive operations",
        SystemPrompt = "You are a planning agent. Analyze the task, read files, and propose a plan. You may NOT edit files or run commands.",
        PermissionRules =
        {
            new() { ToolPattern = "bash", Permission = AgentPermission.Deny },
            new() { ToolPattern = "dotnet build", Permission = AgentPermission.Deny },
            new() { ToolPattern = "git push*", Permission = AgentPermission.Deny },
            new() { ToolPattern = "git commit*", Permission = AgentPermission.Deny },
            new() { ToolPattern = "*delete*", Permission = AgentPermission.Deny },
            new() { ToolPattern = "rm *", Permission = AgentPermission.Deny },
        }
    };

    public static AgentProfile CreateBuild() => new()
    {
        Name = "build",
        Description = "Build agent: full access — edit files, run builds, commit changes",
        SystemPrompt = "You are a build agent. Execute the plan: edit files, run builds, run tests, commit changes when done.",
        PermissionRules =
        {
            new() { ToolPattern = "bash", Permission = AgentPermission.Ask },
            new() { ToolPattern = "git push*", Permission = AgentPermission.Ask },
            new() { ToolPattern = "rm -rf *", Permission = AgentPermission.Deny },
        }
    };

    public static AgentProfile CreateChat() => new()
    {
        Name = "chat",
        Description = "Chat agent: read-only, conversational",
        SystemPrompt = "You are a helpful AI assistant focused on answering questions and brainstorming. You cannot modify files or run commands.",
        PermissionRules =
        {
            new() { ToolPattern = "bash", Permission = AgentPermission.Deny },
            new() { ToolPattern = "dotnet build", Permission = AgentPermission.Deny },
            new() { ToolPattern = "dotnet test", Permission = AgentPermission.Deny },
            new() { ToolPattern = "git*", Permission = AgentPermission.Deny },
            new() { ToolPattern = "*delete*", Permission = AgentPermission.Deny },
            new() { ToolPattern = "*write*", Permission = AgentPermission.Deny },
            new() { ToolPattern = "*edit*", Permission = AgentPermission.Deny },
        }
    };

    public bool CanInvoke(string toolName, string? args = null) =>
        PermissionRules.Count == 0 || PermissionRules
            .Where(r => MatchesGlob(toolName, r.ToolPattern))
            .OrderByDescending(r => r.Priority)
            .FirstOrDefault()?.Permission != AgentPermission.Deny;

    private static bool MatchesGlob(string input, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
        if (pattern.StartsWith("*") && pattern.EndsWith("*"))
            return input.Contains(pattern.Trim('*'));
        if (pattern.StartsWith("*"))
            return input.EndsWith(pattern.TrimStart('*'));
        if (pattern.EndsWith("*"))
            return input.StartsWith(pattern.TrimEnd('*'));
        return string.Equals(input, pattern, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ProfileRule
{
    public string ToolPattern { get; init; } = "";
    public AgentPermission Permission { get; init; } = AgentPermission.Allow;
    public int Priority { get; init; }
}

public enum AgentPermission { Allow, Deny, Ask }
