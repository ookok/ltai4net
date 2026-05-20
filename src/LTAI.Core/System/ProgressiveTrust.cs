using System.Text.Json;

namespace LTAI.Core.System;

public sealed class DomainSkill
{
    public string Domain { get; set; } = "";
    public int TotalInteractions { get; set; }
    public int UserCorrections { get; set; }
    public int UserConfirmations { get; set; }
    public int UserOverrides { get; set; }
    public double LastInteraction { get; set; }
    public int ConsecutiveCorrect { get; set; }
    public double SkillLevel { get; set; } = 0.5;
    public double ConfidenceThreshold { get; set; } = 0.8;
}

public sealed class TrustUserModel
{
    public string Username { get; set; } = "";
    public double FirstSeen { get; set; }
    public double LastSeen { get; set; }
    public int TotalSessions { get; set; }
    public int TotalInteractions { get; set; }
    public Dictionary<string, DomainSkill> Domains { get; set; } = new();
    public List<string> Colleagues { get; set; } = new();
}

public sealed class ProgressiveTrust
{
    private static readonly Lazy<ProgressiveTrust> _instance = new(() => new ProgressiveTrust());
    public static ProgressiveTrust Instance => _instance.Value;

    private readonly Dictionary<string, TrustUserModel> _users = new();
    private readonly string _trustFile;
    private readonly object _lock = new();

    private ProgressiveTrust(string? trustFile = null)
    {
        _trustFile = trustFile ?? global::System.IO.Path.Combine(".livingtree", "user_trust.json");
        var dir = global::System.IO.Path.GetDirectoryName(_trustFile);
        if (dir != null) global::System.IO.Directory.CreateDirectory(dir);
        Load();
    }

    public void RecordInteraction(string username, string domain,
        bool userCorrected = false, bool userConfirmed = false, bool userOverrode = false)
    {
        if (string.IsNullOrEmpty(username)) return;

        lock (_lock)
        {
            if (!_users.TryGetValue(username, out var u))
            {
                u = new TrustUserModel
                {
                    Username = username,
                    FirstSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                _users[username] = u;
            }

            u.LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            u.TotalInteractions++;

            if (!u.Domains.TryGetValue(domain, out var ds))
            {
                ds = new DomainSkill { Domain = domain };
                u.Domains[domain] = ds;
            }

            ds.TotalInteractions++;
            ds.LastInteraction = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (userCorrected)
            {
                ds.UserCorrections++;
                ds.ConsecutiveCorrect = 0;
            }
            else if (userConfirmed)
            {
                ds.UserConfirmations++;
                ds.ConsecutiveCorrect++;
            }
            if (userOverrode)
                ds.UserOverrides++;

            ds.SkillLevel = CalcSkill(ds);
            ds.ConfidenceThreshold = CalcThreshold(ds);
        }

        MaybeSave();
    }

    public string ExpertiseLevel(string username, string domain)
    {
        var ds = GetDomain(username, domain);
        if (ds == null || ds.TotalInteractions < 3)
            return "unknown";
        if (ds.SkillLevel >= 0.8) return "expert";
        if (ds.SkillLevel >= 0.6) return "proficient";
        if (ds.SkillLevel >= 0.3) return "learning";
        return "novice";
    }

    public bool ShouldConfirm(string username, string domain, double confidence = 0.5)
    {
        var ds = GetDomain(username, domain);
        if (ds == null) return true;

        if (ds.SkillLevel >= 0.8)
            return confidence < 0.3;
        if (ds.SkillLevel >= 0.6)
            return confidence < ds.ConfidenceThreshold;
        return confidence < Math.Max(ds.ConfidenceThreshold, 0.7);
    }

    public Dictionary<string, object>? GetUserProfile(string username)
    {
        TrustUserModel? u;
        lock (_lock)
        {
            if (!_users.TryGetValue(username, out u)) return null;
        }

        var expertise = new Dictionary<string, object>();
        foreach (var (domain, ds) in u.Domains)
        {
            if (ds.TotalInteractions < 3) continue;
            expertise[domain] = new Dictionary<string, object>
            {
                ["level"] = ExpertiseLevel(username, domain),
                ["skill"] = Math.Round(ds.SkillLevel, 2),
                ["interactions"] = ds.TotalInteractions,
                ["correction_rate"] = Math.Round((double)ds.UserCorrections / Math.Max(ds.TotalInteractions, 1) * 100, 1),
                ["consecutive_ok"] = ds.ConsecutiveCorrect
            };
        }

        return new Dictionary<string, object>
        {
            ["username"] = u.Username,
            ["sessions"] = u.TotalSessions,
            ["interactions"] = u.TotalInteractions,
            ["expertise"] = expertise,
            ["auto_approval_domains"] = u.Domains
                .Where(kv => ExpertiseLevel(username, kv.Key) == "expert")
                .Select(kv => kv.Key)
                .ToList()
        };
    }

    public void LinkColleague(string username, string colleague)
    {
        lock (_lock)
        {
            if (_users.TryGetValue(username, out var u) && _users.TryGetValue(colleague, out var c))
            {
                if (!u.Colleagues.Contains(colleague))
                    u.Colleagues.Add(colleague);
                if (!c.Colleagues.Contains(username))
                    c.Colleagues.Add(username);
                Save();
            }
        }
    }

    private DomainSkill? GetDomain(string username, string domain)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(username, out var u)) return null;
            if (u.Domains.TryGetValue(domain, out var ds)) return ds;

            foreach (var (d, skill) in u.Domains)
            {
                if (d.Contains(domain) || domain.Contains(d))
                    return skill;
            }
        }
        return null;
    }

    private static double CalcSkill(DomainSkill ds)
    {
        var total = ds.TotalInteractions;
        if (total < 3) return 0.5;

        var correctRate = (double)ds.UserConfirmations / Math.Max(total, 1);
        var correctionPenalty = (double)ds.UserCorrections / Math.Max(total, 1);
        var streakBonus = Math.Min(ds.ConsecutiveCorrect / 10.0, 0.2);

        return Math.Min(1.0, correctRate * 0.7 + (1 - correctionPenalty) * 0.3 + streakBonus);
    }

    private static double CalcThreshold(DomainSkill ds)
    {
        if (ds.TotalInteractions < 3) return 0.8;
        return Math.Max(0.3, 0.8 - ds.SkillLevel * 0.5);
    }

    private void Save()
    {
        var data = new Dictionary<string, object>();
        lock (_lock)
        {
            foreach (var (username, u) in _users)
            {
                data[username] = new Dictionary<string, object>
                {
                    ["username"] = u.Username,
                    ["first_seen"] = u.FirstSeen,
                    ["last_seen"] = u.LastSeen,
                    ["total_sessions"] = u.TotalSessions,
                    ["total_interactions"] = u.TotalInteractions,
                    ["colleagues"] = u.Colleagues,
                    ["domains"] = u.Domains.ToDictionary(
                        d => d.Key,
                        d => (object)new Dictionary<string, object>
                        {
                            ["domain"] = d.Value.Domain,
                            ["total_interactions"] = d.Value.TotalInteractions,
                            ["user_corrections"] = d.Value.UserCorrections,
                            ["user_confirmations"] = d.Value.UserConfirmations,
                            ["user_overrides"] = d.Value.UserOverrides,
                            ["last_interaction"] = d.Value.LastInteraction,
                            ["consecutive_correct"] = d.Value.ConsecutiveCorrect,
                            ["skill_level"] = d.Value.SkillLevel,
                            ["confidence_threshold"] = d.Value.ConfidenceThreshold
                        })
                };
            }
        }

        global::System.IO.File.WriteAllText(_trustFile, JsonSerializer.Serialize(data));
    }

    private void Load()
    {
        if (!global::System.IO.File.Exists(_trustFile)) return;
        try
        {
            var json = global::System.IO.File.ReadAllText(_trustFile);
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (data == null) return;

            foreach (var (username, ud) in data)
            {
                var u = new TrustUserModel
                {
                    Username = ud.TryGetProperty("username", out var un) ? un.GetString() ?? username : username,
                    FirstSeen = ud.TryGetProperty("first_seen", out var fs) ? fs.GetDouble() : 0,
                    LastSeen = ud.TryGetProperty("last_seen", out var ls) ? ls.GetDouble() : 0,
                    TotalSessions = ud.TryGetProperty("total_sessions", out var ts) ? ts.GetInt32() : 0,
                    TotalInteractions = ud.TryGetProperty("total_interactions", out var ti) ? ti.GetInt32() : 0,
                };

                if (ud.TryGetProperty("colleagues", out var colleagues))
                    u.Colleagues = colleagues.EnumerateArray().Select(c => c.GetString() ?? "").ToList();

                if (ud.TryGetProperty("domains", out var domains))
                {
                    foreach (var dp in domains.EnumerateObject())
                    {
                        var dd = dp.Value;
                        u.Domains[dp.Name] = new DomainSkill
                        {
                            Domain = dd.TryGetProperty("domain", out var d) ? d.GetString() ?? dp.Name : dp.Name,
                            TotalInteractions = dd.TryGetProperty("total_interactions", out var dti) ? dti.GetInt32() : 0,
                            UserCorrections = dd.TryGetProperty("user_corrections", out var uc) ? uc.GetInt32() : 0,
                            UserConfirmations = dd.TryGetProperty("user_confirmations", out var ucf) ? ucf.GetInt32() : 0,
                            UserOverrides = dd.TryGetProperty("user_overrides", out var uo) ? uo.GetInt32() : 0,
                            LastInteraction = dd.TryGetProperty("last_interaction", out var li) ? li.GetDouble() : 0,
                            ConsecutiveCorrect = dd.TryGetProperty("consecutive_correct", out var cc) ? cc.GetInt32() : 0,
                            SkillLevel = dd.TryGetProperty("skill_level", out var sl) ? sl.GetDouble() : 0.5,
                            ConfidenceThreshold = dd.TryGetProperty("confidence_threshold", out var ct) ? ct.GetDouble() : 0.8
                        };
                    }
                }

                _users[username] = u;
            }
        }
        catch { /* non-fatal */ }
    }

    private void MaybeSave()
    {
        var total = 0;
        lock (_lock) { total = _users.Values.Sum(u => u.TotalInteractions); }
        if (total % 20 == 0) Save();
    }
}
