using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LTAI.Planning.HTN;

public enum PlanNodeType { Task, SubTask, ToolCall, Decision, Parallel }

public sealed record PlanNode
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public PlanNodeType Type { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public List<string> ToolCalls { get; init; } = new();
    public List<PlanNode> Children { get; init; } = new();
    public Dictionary<string, string> Parameters { get; init; } = new();
    public string? ParentPlanId { get; set; }
    public bool IsReusable { get; set; } = true;
    public bool Success { get; set; }
    public int ReuseCount { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
}

public sealed record PlanTemplate
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Domain { get; init; } = "";
    public string IntentHash { get; init; } = "";
    public List<string> SubPlanIds { get; init; } = new();
    public string Skeleton { get; init; } = "";
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public double SuccessRate => SuccessCount + FailureCount > 0
        ? (double)SuccessCount / (SuccessCount + FailureCount) : 0;
    public List<string> Tags { get; init; } = new();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
}

public sealed class HTNPlanner
{
    private readonly ILogger<HTNPlanner> _logger;
    private readonly Dictionary<string, PlanNode> _planLibrary = new();
    private readonly Dictionary<string, PlanTemplate> _templates = new();
    private readonly Dictionary<string, List<string>> _domainIndex = new();
    private readonly object _lock = new();
    private const int MaxPlanLibrary = 2000;

    public HTNPlanner(ILogger<HTNPlanner> logger)
    {
        _logger = logger;
    }

    public PlanNode DecomposeWithValidation(string task, string domain, List<string> availableTools, int maxRetries = 3)
    {
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            var plan = DecomposeTask(task, domain, availableTools);
            if (ValidatePlan(plan, availableTools)) return plan;

            _logger.LogWarning("HTN: Plan validation failed (attempt {Attempt}/{Max}), retrying with broader domain",
                attempt + 1, maxRetries);
            domain = "general";
        }

        _logger.LogError("HTN: All {Max} decomposition attempts failed for task '{Task}'", maxRetries, task);
        return new PlanNode
        {
            Id = $"fallback_{Guid.NewGuid():N}"[..12],
            Type = PlanNodeType.Task,
            Name = "fallback",
            Description = task,
            Children = new List<PlanNode>
            {
                new() { Type = PlanNodeType.ToolCall, Name = "direct_execution", Description = task,
                    ToolCalls = availableTools.Take(5).ToList() }
            }
        };
    }

    private static bool ValidatePlan(PlanNode plan, List<string> availableTools)
    {
        if (plan.Children.Count == 0) return false;

        foreach (var child in plan.Children)
        {
            if (child.Type == PlanNodeType.ToolCall && child.ToolCalls.Count == 0)
                return false;

            if (child.Type == PlanNodeType.SubTask && child.ToolCalls.Count == 0 &&
                availableTools.Count == 0)
            {
                return false;
            }
        }
        return true;
    }

    public PlanNode DecomposeTask(string task, string domain, List<string> availableTools)
    {
        var rootId = $"root_{Guid.NewGuid():N}"[..12];
        var root = new PlanNode
        {
            Id = rootId, Type = PlanNodeType.Task,
            Name = domain, Description = task
        };

        var template = FindBestTemplate(task, domain);
        if (template != null)
        {
            _logger.LogInformation("HTN: Reusing template {Template} for domain {Domain}", template.Name, domain);
            return InstantiateTemplate(template, rootId, task);
        }

        var subTasks = DecomposeByPattern(task, domain);
        foreach (var (name, desc, tools) in subTasks)
        {
            var subPlan = FindBestSubPlan(name, domain);
            if (subPlan != null)
            {
                _logger.LogInformation("HTN: Reusing sub-plan {Name} for domain {Domain}", subPlan.Name, domain);
                subPlan.ParentPlanId = rootId;
                subPlan.ReuseCount++;
                subPlan.LastUsedAt = DateTime.UtcNow;
                root.Children.Add(subPlan);
            }
            else
            {
                root.Children.Add(new PlanNode
                {
                    Type = PlanNodeType.SubTask, Name = name,
                    Description = desc, ToolCalls = tools,
                    ParentPlanId = rootId
                });
            }
        }

        if (root.Children.Count == 0)
        {
            root.Children.Add(new PlanNode
            {
                Type = PlanNodeType.ToolCall, Name = "direct_execution",
                Description = task,
                ToolCalls = availableTools.Take(3).ToList(),
                ParentPlanId = rootId
            });
        }

        return root;
    }

    public (PlanNode Primary, List<PlanNode> Alternatives) DecomposeWithAlternatives(
        string task, string domain, List<string> availableTools, int maxAlternatives = 2)
    {
        var primary = DecomposeTask(task, domain, availableTools);
        var alternatives = new List<PlanNode>();

        var templates = _templates.Values
            .Where(t => t.Domain == domain && t.Id != (primary.Children.FirstOrDefault()?.Id ?? ""))
            .OrderByDescending(t => t.SuccessRate)
            .Take(maxAlternatives)
            .ToList();

        foreach (var alt in templates)
        {
            var altPlan = InstantiateTemplate(alt, $"alt_{Guid.NewGuid():N}"[..12], task);
            alternatives.Add(altPlan);
        }

        if (alternatives.Count == 0 && primary.Children.Count > 1)
        {
            var reversed = new PlanNode
            {
                Id = $"alt_rev_{Guid.NewGuid():N}"[..12],
                Type = PlanNodeType.Task,
                Name = domain,
                Description = $"Alternative: {task}",
                Children = primary.Children.AsEnumerable().Reverse().Select(c => c with { }).ToList()
            };
            alternatives.Add(reversed);
        }

        _logger.LogInformation("HTN: Generated {Count} alternatives for domain {Domain}",
            alternatives.Count, domain);

        return (primary, alternatives);
    }

    public void StorePlan(PlanNode plan, bool success)
    {
        lock (_lock)
        {
            plan.Success = success;
            _planLibrary[plan.Id] = plan;

            foreach (var child in plan.Children)
                _planLibrary[child.Id] = child;

            var domain = plan.Name;
            if (!_domainIndex.ContainsKey(domain))
                _domainIndex[domain] = new List<string>();
            _domainIndex[domain].Add(plan.Id);

            if (success && plan.IsReusable)
            {
                var template = CreateTemplate(plan);
                _templates[template.Id] = template;
            }

            if (_planLibrary.Count > MaxPlanLibrary)
            {
                var toRemove = _planLibrary.Values
                    .OrderBy(p => p.LastUsedAt)
                    .Take(200)
                    .Select(p => p.Id)
                    .ToList();
                foreach (var id in toRemove) _planLibrary.Remove(id);
            }
        }
    }

    public PlanNode? FindBestSubPlan(string taskName, string domain)
    {
        lock (_lock)
        {
            var candidates = _planLibrary.Values
                .Where(p => p.Name == taskName && p.IsReusable && p.Success)
                .OrderByDescending(p => p.ReuseCount)
                .ThenByDescending(p => p.LastUsedAt)
                .ToList();

            return candidates.FirstOrDefault();
        }
    }

    public PlanTemplate? FindBestTemplate(string task, string domain)
    {
        lock (_lock)
        {
            var hash = ComputeIntentHash($"{domain}:{task}");

            var byHash = _templates.Values
                .FirstOrDefault(t => t.IntentHash == hash && t.Domain == domain);
            if (byHash != null) return byHash;

            return _templates.Values
                .Where(t => t.Domain == domain)
                .OrderByDescending(t => t.SuccessRate)
                .ThenByDescending(t => t.LastUsedAt)
                .FirstOrDefault();
        }
    }

    public List<PlanTemplate> GetTemplatesByDomain(string domain)
    {
        lock (_lock)
        {
            return _templates.Values
                .Where(t => t.Domain == domain)
                .OrderByDescending(t => t.SuccessRate)
                .ToList();
        }
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            return new()
            {
                ["total_plans"] = _planLibrary.Count,
                ["total_templates"] = _templates.Count,
                ["domains"] = _domainIndex.Keys.ToList(),
                ["avg_template_success_rate"] = _templates.Values.Count > 0
                    ? _templates.Values.Average(t => t.SuccessRate) : 0,
                ["top_templates"] = _templates.Values
                    .OrderByDescending(t => t.SuccessCount)
                    .Take(5)
                    .Select(t => new { t.Name, t.Domain, t.SuccessRate, t.SuccessCount }),
                ["most_reused_plans"] = _planLibrary.Values
                    .Where(p => p.ReuseCount > 0)
                    .OrderByDescending(p => p.ReuseCount)
                    .Take(5)
                    .Select(p => new { p.Name, p.ReuseCount, p.Success })
            };
        }
    }

    private PlanNode InstantiateTemplate(PlanTemplate template, string rootId, string task)
    {
        var root = new PlanNode
        {
            Id = rootId, Type = PlanNodeType.Task,
            Name = template.Domain, Description = task
        };

        foreach (var subPlanId in template.SubPlanIds)
        {
            if (_planLibrary.TryGetValue(subPlanId, out var subPlan))
            {
                subPlan.ReuseCount++;
                subPlan.LastUsedAt = DateTime.UtcNow;
                root.Children.Add(subPlan);
            }
        }

        template.LastUsedAt = DateTime.UtcNow;
        template.SuccessCount++;
        return root;
    }

    private List<(string name, string desc, List<string> tools)> DecomposeByPattern(
        string task, string domain)
    {
        var result = new List<(string, string, List<string>)>();
        var lower = task.ToLowerInvariant();

        var patterns = new[]
        {
            ("analyze", "Analysis", new List<string> { "code_analyze", "km_search" }),
            ("build", "Build & Compile", new List<string> { "code_build:run", "shell" }),
            ("test", "Testing", new List<string> { "code_test:run", "code_test:affected" }),
            ("review", "Review & Quality", new List<string> { "code_review", "code_graph:blast_radius" }),
            ("refactor", "Refactoring", new List<string> { "code_analyze", "code_edit:replace_function" }),
            ("deploy", "Deployment", new List<string> { "shell", "git" }),
            ("document", "Documentation", new List<string> { "doc_parse", "text_extract" }),
            ("search", "Search & Retrieval", new List<string> { "km_search", "vector_search" }),
            ("eia", "Environmental Assessment", new List<string> { "gaussian_plume", "noise_iso9613", "km_search" }),
            ("environmental", "Environmental Assessment", new List<string> { "gaussian_plume", "noise_iso9613" }),
            ("report", "Report Generation", new List<string> { "report_generate", "doc_parse" }),
            ("compare", "Comparison Analysis", new List<string> { "km_search", "web_fetch" }),
            ("calculate", "Computation", new List<string> { "math", "km_search" }),
            ("summarize", "Summarization", new List<string> { "km_search", "rag_ask" }),
            ("generate", "Generation", new List<string> { "code_analyze", "shell" })
        };

        foreach (var (keyword, name, tools) in patterns)
        {
            if (lower.Contains(keyword))
                result.Add((name, $"Auto-decomposed: {keyword}", tools));
        }

        if (result.Count == 0)
            result.Add(("General Processing", task, new List<string> { "km_search", "shell" }));

        return result.Take(5).ToList();
    }

    private PlanTemplate CreateTemplate(PlanNode plan)
    {
        var id = $"tmpl_{Guid.NewGuid():N}"[..12];
        var subPlanIds = plan.Children.Select(c => c.Id).ToList();
        var skeleton = BuildSkeleton(plan);
        var intentHash = ComputeIntentHash($"{plan.Name}:{plan.Description}");

        return new PlanTemplate
        {
            Id = id, Name = plan.Name, Domain = plan.Name,
            IntentHash = intentHash, SubPlanIds = subPlanIds,
            Skeleton = skeleton, SuccessCount = 1
        };
    }

    private static string BuildSkeleton(PlanNode node, int depth = 0)
    {
        var indent = new string(' ', depth * 2);
        var sb = new StringBuilder();
        sb.AppendLine($"{indent}[{node.Type}] {node.Name}");
        foreach (var tool in node.ToolCalls)
            sb.AppendLine($"{indent}  └─ {tool}");
        foreach (var child in node.Children)
            sb.Append(BuildSkeleton(child, depth + 1));
        return sb.ToString();
    }

    private static string ComputeIntentHash(string input) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];
}
