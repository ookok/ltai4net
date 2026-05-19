using LTAI.DNA.Models;

namespace LTAI.DNA.Life;

public sealed class SheshaHeads
{
    private readonly Dictionary<string, SheshaHeadState> _heads = new();
    private readonly List<InterHeadMessage> _messages = new();
    private readonly Dictionary<string, int> _inactiveCycles = new();
    private int _totalDelegations;
    private readonly object _lock = new();

    private static readonly Dictionary<HeadRole, Dictionary<string, double>> RoleTraits = new()
    {
        [HeadRole.CodeAssistant] = new()
            { ["precision"] = 0.8, ["persistence"] = 0.7, ["creativity"] = 0.5, ["caution"] = 0.6 },
        [HeadRole.ResearchAid] = new()
            { ["curiosity"] = 0.9, ["openness"] = 0.8, ["creativity"] = 0.7, ["precision"] = 0.5 },
        [HeadRole.SocialAgent] = new()
            { ["empathy"] = 0.9, ["openness"] = 0.7, ["caution"] = 0.4, ["persistence"] = 0.5 },
        [HeadRole.OpsAgent] = new()
            { ["precision"] = 0.8, ["caution"] = 0.7, ["persistence"] = 0.8, ["creativity"] = 0.3 },
        [HeadRole.Critic] = new()
            { ["precision"] = 0.9, ["caution"] = 0.7, ["creativity"] = 0.5, ["empathy"] = 0.3 },
        [HeadRole.Planner] = new()
            { ["creativity"] = 0.7, ["persistence"] = 0.8, ["openness"] = 0.6, ["precision"] = 0.5 },
        [HeadRole.Teacher] = new()
            { ["empathy"] = 0.8, ["openness"] = 0.7, ["persistence"] = 0.7, ["caution"] = 0.4 },
        [HeadRole.Explorer] = new()
            { ["curiosity"] = 1.0, ["openness"] = 0.9, ["creativity"] = 0.8, ["caution"] = 0.2 }
    };

    public SheshaHeads()
    {
        InitDefaultHeads();
    }

    private void InitDefaultHeads()
    {
        CreateHead("DeepThink", HeadRole.ResearchAid);
        CreateHead("CodeWise", HeadRole.CodeAssistant);
        CreateHead("SocialEye", HeadRole.SocialAgent);
        CreateHead("OpsGuard", HeadRole.OpsAgent);
        CreateHead("SharpCrit", HeadRole.Critic);
        CreateHead("PlanMaster", HeadRole.Planner);
        CreateHead("WisdomShare", HeadRole.Teacher);
        CreateHead("CurioSeek", HeadRole.Explorer);
    }

    public SheshaHeadState CreateHead(string name, HeadRole role)
    {
        var traits = new Dictionary<string, double>(RoleTraits.GetValueOrDefault(role, new()));
        var rand = new Random();
        foreach (var key in traits.Keys.ToList())
            traits[key] = Math.Clamp(traits[key] + (rand.NextDouble() - 0.5) * 0.2, 0, 1);

        var head = new SheshaHeadState { Name = name, Role = role, Phase = HeadPhase.Newborn };
        foreach (var (k, v) in traits) head.Traits[k] = v;

        lock (_lock)
        {
            _heads[head.Id] = head;
            _inactiveCycles[head.Id] = 0;
        }

        return head;
    }

    public SheshaHeadState? GetHead(string id)
    {
        lock (_lock) return _heads.GetValueOrDefault(id);
    }

    public List<SheshaHeadState> ListHeads(HeadRole? role = null)
    {
        lock (_lock)
        {
            return role.HasValue
                ? _heads.Values.Where(h => h.Role == role.Value).ToList()
                : _heads.Values.ToList();
        }
    }

    public bool RemoveHead(string id)
    {
        lock (_lock) return _heads.Remove(id);
    }

    public InterHeadMessage SendMessage(string fromId, string toId, string type, string content)
    {
        var msg = new InterHeadMessage { From = fromId, To = toId, Type = type, Content = content };
        lock (_lock)
        {
            _messages.Add(msg);
            if (_messages.Count > 5000) _messages.RemoveAt(0);
        }

        return msg;
    }

    public async Task<Dictionary<string, object>> DelegateTask(string description, HeadRole? preferredRole = null)
    {
        List<SheshaHeadState> candidates;
        lock (_lock) { candidates = _heads.Values.ToList(); }

        if (preferredRole.HasValue)
            candidates = candidates.Where(h => h.Role == preferredRole.Value).ToList();
        if (candidates.Count == 0)
            candidates = _heads.Values.ToList();

        var scored = candidates
            .Select(h => new
            {
                Head = h,
                Score = (h.Role == (preferredRole ?? h.Role) ? 0.4 : 0)
                        + h.SuccessRate * 0.4
                        + (int)h.Phase * 0.2 / 4.0
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        if (scored.Count == 0)
            return new Dictionary<string, object> { ["error"] = "No heads available" };

        var best = scored.First();
        _totalDelegations++;

        return new Dictionary<string, object>
        {
            ["head_id"] = best.Head.Id,
            ["head_name"] = best.Head.Name,
            ["score"] = best.Score,
            ["total_delegations"] = _totalDelegations
        };
    }

    public async Task<Dictionary<string, object>> RunHeadTask(string headId, string task, Func<string, Task<string>> chat)
    {
        var head = GetHead(headId);
        if (head == null) return new Dictionary<string, object> { ["error"] = "Head not found" };

        var prompt = $"As {head.Name} ({head.Role}), a {head.Phase} phase agent:\n" +
                     $"My traits: {string.Join(", ", head.Traits.Select(kv => $"{kv.Key}={kv.Value:F2}"))}\n" +
                     $"Task: {task}";

        var result = await chat(prompt);
        var success = !string.IsNullOrEmpty(result) && result.Length > 10;

        EvolveHead(head, "task", success);
        head.TotalTasks++;
        if (success) head.SuccessfulTasks++;

        return new Dictionary<string, object>
        {
            ["head_id"] = headId,
            ["result"] = result ?? "",
            ["success"] = success,
            ["phase"] = head.Phase.ToString()
        };
    }

    public async Task<Dictionary<string, object>> InterHeadCollaboration(string task, List<string> headIds,
        Func<string, Task<string>> chat)
    {
        if (headIds.Count < 2)
            return new Dictionary<string, object> { ["error"] = "Need at least 2 heads" };

        var heads = headIds.Select(GetHead).OfType<SheshaHeadState>().ToList();
        if (heads.Count < 2)
            return new Dictionary<string, object> { ["error"] = "Not enough valid heads" };

        var results = new List<string>();
        foreach (var head in heads)
        {
            var msg = SendMessage(head.Name, string.Join(",", heads.Where(h => h.Id != head.Id).Select(h => h.Name)),
                "collaboration", task);
            var outcome = await RunHeadTask(head.Id, task, chat);
            results.Add($"{head.Name}: {outcome.GetValueOrDefault("result", "")}");
        }

        var synthesis = await chat(
            $"Synthesize these perspectives on the task '{task}':\n{string.Join("\n---\n", results)}");

        return new Dictionary<string, object>
        {
            ["participants"] = headIds.Count,
            ["individual_results"] = results,
            ["synthesis"] = synthesis ?? "",
            ["cooperation_score"] = 0.5 + results.Count * 0.1
        };
    }

    private void EvolveHead(SheshaHeadState head, string eventType, bool success)
    {
        const double learningRate = 0.02;
        const double decay = 0.001;

        var traitKeys = head.Traits.Keys.ToList();
        if (success)
        {
            foreach (var key in traitKeys)
                head.Traits[key] = Math.Clamp(head.Traits[key] + learningRate * 0.3, 0, 1);
        }
        else
        {
            foreach (var key in traitKeys)
                head.Traits[key] = Math.Clamp(head.Traits[key] - learningRate * 0.2, 0, 1);
        }

        foreach (var key in traitKeys)
            head.Traits[key] = Math.Clamp(head.Traits[key] - decay, 0, 1);

        PromotePhase(head);
    }

    private void PromotePhase(SheshaHeadState head)
    {
        head.Phase = head switch
        {
            { Phase: HeadPhase.Newborn, TotalTasks: >= 10 } => HeadPhase.Apprentice,
            { Phase: HeadPhase.Apprentice, SuccessfulTasks: >= 50 } => HeadPhase.Journeyman,
            { Phase: HeadPhase.Journeyman, SuccessfulTasks: >= 200 } => HeadPhase.Master,
            _ => head.Phase
        };
    }

    public Dictionary<string, object> Stats()
    {
        lock (_lock)
        {
            return new Dictionary<string, object>
            {
                ["total_heads"] = _heads.Count,
                ["total_messages"] = _messages.Count,
                ["total_delegations"] = _totalDelegations,
                ["heads_by_phase"] = _heads.Values.GroupBy(h => h.Phase)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count()),
                ["heads_by_role"] = _heads.Values.GroupBy(h => h.Role)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count())
            };
        }
    }

    public string GetSocietySummary()
    {
        lock (_lock)
        {
            var lines = new List<string> { $"Shesha Society: {_heads.Count} heads" };
            foreach (var head in _heads.Values.OrderBy(h => h.Phase))
                lines.Add(
                    $"  [{head.Phase}] {head.Name} ({head.Role}) - {head.SuccessfulTasks}/{head.TotalTasks} tasks, SR={head.SuccessRate:F2}");
            return string.Join("\n", lines);
        }
    }

    public Dictionary<string, object> EvolveSociety()
    {
        lock (_lock)
        {
            var promoted = 0;
            var pruned = 0;
            var headIds = _heads.Keys.ToList();

            foreach (var id in headIds)
            {
                var head = _heads[id];
                var prevPhase = head.Phase;
                PromotePhase(head);
                if (head.Phase != prevPhase) promoted++;

                _inactiveCycles[id] = head.TotalTasks == 0 ? _inactiveCycles[id] + 1 : 0;
                if (_inactiveCycles[id] > 500 && head.Phase < HeadPhase.Master)
                {
                    _heads.Remove(id);
                    _inactiveCycles.Remove(id);
                    pruned++;
                }
            }

            var heads = _heads.Values.ToList();
            for (int i = 0; i < heads.Count; i++)
            for (int j = i + 1; j < heads.Count; j++)
            {
                if (heads[i].Role != heads[j].Role && heads[i].SuccessRate > 0.7 &&
                    heads[j].SuccessRate > 0.7)
                {
                    heads[i].Collaborators[heads[j].Id] =
                        heads[i].Collaborators.GetValueOrDefault(heads[j].Id) + 1;
                    heads[j].Collaborators[heads[i].Id] =
                        heads[j].Collaborators.GetValueOrDefault(heads[i].Id) + 1;
                }
            }

            return new Dictionary<string, object>
            {
                ["promoted"] = promoted, ["pruned"] = pruned, ["remaining"] = _heads.Count
            };
        }
    }
}
