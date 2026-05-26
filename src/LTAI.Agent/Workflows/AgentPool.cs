using System.Collections.Concurrent;
using LTAI.AI.Interfaces;
using LTAI.Knowledge.Core;
using LTAI.Models;

namespace LTAI.Agent.Workflows;

public sealed class AgentPool
{
    private readonly ILivingTreeSystem _lts;
    private readonly PromptService? _promptService;
    private readonly Dictionary<string, TeamMember> _members = new();
    private readonly ConcurrentDictionary<string, string> _promptCache = new();
    private readonly object _lock = new();
    private LTAICoordinator? _parentCoordinator;
    private bool _warmed;

    public IReadOnlyDictionary<string, TeamMember> Members
    {
        get { lock (_lock) return new Dictionary<string, TeamMember>(_members); }
    }

    public bool IsWarmed => _warmed;
    public int MemberCount { get { lock (_lock) return _members.Count; } }

    public AgentPool(ILivingTreeSystem lts, PromptService? promptService = null)
    {
        _lts = lts;
        _promptService = promptService;
    }

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        if (_warmed) return;

        await WithDefaultMembersAsync(ct).ConfigureAwait(false);

        foreach (var member in _members.Values)
        {
            if (!string.IsNullOrEmpty(member.SystemPrompt))
                _promptCache[member.Name] = BuildFullPrompt(member.SystemPrompt, "");
        }

        _warmed = true;
    }

    public async Task WithDefaultMembersAsync(CancellationToken ct = default)
    {
        var defaults = new TeamMember[]
        {
            new() { Name = "code", Role = "Code analysis and editing", SystemPrompt = GetDefaultPrompt("code") },
            new() { Name = "eia", Role = "Environmental impact assessment and modeling", SystemPrompt = GetDefaultPrompt("eia") },
            new() { Name = "chat", Role = "Conversation and general knowledge", SystemPrompt = GetDefaultPrompt("chat") },
            new() { Name = "reasoning", Role = "Logic, planning, and reasoning", SystemPrompt = GetDefaultPrompt("reasoning") }
        };

        Register(defaults);
        await Task.CompletedTask;
    }

    private static string GetDefaultPrompt(string role) => role switch
    {
        "code" => "You are a software engineering agent. Analyze, edit, and build code. Focus on correctness and existing conventions.",
        "eia" => "You are an environmental impact assessment agent. Use EIA models, standards (GB/HJ), and GIS tools for analysis.",
        "chat" => "You are a conversational agent. Answer questions clearly and concisely. Use tools when needed for factual accuracy.",
        "reasoning" => "You are a logic and reasoning agent. Break down complex problems, evaluate alternatives, and recommend optimal solutions.",
        _ => ""
    };

    public void Register(TeamMember member)
    {
        lock (_lock)
        {
            _members[member.Name] = member;
            _promptCache.TryRemove(member.Name, out _);
        }
    }

    public void Register(IEnumerable<TeamMember> members)
    {
        lock (_lock)
        {
            foreach (var m in members)
            {
                _members[m.Name] = m;
                _promptCache.TryRemove(m.Name, out _);
            }
        }
    }

    public TeamMember? Get(string name)
    {
        lock (_lock)
        {
            _members.TryGetValue(name, out var m);
            return m;
        }
    }

    public async Task<string> RunAgentAsync(
        string agentName,
        string prompt,
        CancellationToken ct = default)
    {
        var member = Get(agentName);
        var systemPrompt = member?.SystemPrompt ?? "";

        if (_promptCache.TryGetValue(agentName, out var cachedFull))
        {
            var isReusable = string.IsNullOrEmpty(systemPrompt) || cachedFull.StartsWith(systemPrompt);
            if (isReusable && !string.IsNullOrEmpty(prompt))
                return await _lts.ChatAsync($"{cachedFull}\n\nTask: {prompt}", ct).ConfigureAwait(false);
        }

        var fullPrompt = BuildFullPrompt(systemPrompt, prompt);
        return await _lts.ChatAsync(fullPrompt, ct).ConfigureAwait(false);
    }

    private static string BuildFullPrompt(string systemPrompt, string task)
    {
        return string.IsNullOrEmpty(systemPrompt)
            ? task
            : $"[System: {systemPrompt}]\n\nTask: {task}";
    }

    public async Task RunParallelAsync(
        IEnumerable<(string TaskId, string AgentName, string Prompt)> batch,
        Dictionary<string, string> results,
        SemaphoreSlim semaphore,
        CancellationToken ct = default)
    {
        var tasks = batch.Select(async item =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var output = await RunAgentAsync(item.AgentName, item.Prompt, ct).ConfigureAwait(false);
                lock (results)
                    results[item.TaskId] = output;
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<string> SynthesizeResultsAsync(
        string goal,
        IReadOnlyDictionary<string, string> results,
        CancellationToken ct = default)
    {
        if (results.Count == 0)
            return "No results to synthesize.";

        var parts = string.Join("\n\n---\n\n",
            results.Select(kv =>
                $"[Task: {kv.Key}]\n{kv.Value}"));

        var prompt = BuildSynthesisPrompt(goal, parts);

        return await _lts.ChatAsync(prompt, ct).ConfigureAwait(false);
    }

    private string BuildSynthesisPrompt(string goal, string parts)
    {
        if (_promptService != null)
        {
            var promptId = _promptService.GetBestForTask("synthesize task results coordinator team",
                "coordinator");
            if (promptId != null)
            {
                var rendered = _promptService.Render(promptId.Id, new()
                {
                    ["goal"] = goal,
                    ["results"] = parts
                });
                if (rendered.Success)
                    return rendered.Rendered;
            }
        }

        return $"""
            你是一个团队的协调员。以下是团队各成员为达成目标 "{goal}" 的执行结果：

            {parts}

            请综合分析上述所有结果，生成一个完整、连贯的最终输出。
            如果结果之间存在矛盾，请明确指出并给出最合理的结论。
            输出格式：纯文本，不需要特殊标记。
            """;
    }

    public void SetParentCoordinator(LTAICoordinator coordinator)
    {
        _parentCoordinator = coordinator;
    }
}
