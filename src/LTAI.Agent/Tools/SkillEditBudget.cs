using System.Collections.Concurrent;
using System.Text.Json;

namespace LTAI.Agent.Tools;

public sealed record BudgetEntry
{
    public double RemainingRatio { get; set; } = 1.0;
    public double MaxChangeRatio { get; set; } = 0.3;
    public double RefillPerDay { get; set; } = 0.1;
    public DateTime LastRefilled { get; set; } = DateTime.UtcNow;
    public int EditCount { get; set; }
}

public sealed class SkillEditBudget
{
    private readonly ConcurrentDictionary<string, BudgetEntry> _budgets = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _storePath;
    private readonly object _saveLock = new();

    public SkillEditBudget(string skillsDir)
    {
        _storePath = Path.Combine(skillsDir, ".skillopt", "budgets.json");
        Load();
    }

    public bool TrySpend(string skillName, string oldContent, string newContent)
    {
        Refill(skillName);

        if (!_budgets.TryGetValue(skillName, out var entry))
        {
            entry = new BudgetEntry();
            _budgets[skillName] = entry;
        }

        if (string.IsNullOrEmpty(oldContent))
            return true;

        var oldTokens = EstimateTokens(oldContent);
        var newTokens = EstimateTokens(newContent);
        if (oldTokens <= 0) return true;

        var changeRatio = Math.Abs(newTokens - oldTokens) / (double)oldTokens;

        if (changeRatio > entry.RemainingRatio)
            return false;

        entry.RemainingRatio -= changeRatio;
        entry.EditCount++;
        Save();
        return true;
    }

    public double GetRemainingBudget(string skillName)
    {
        Refill(skillName);
        return _budgets.TryGetValue(skillName, out var entry) ? entry.RemainingRatio : 1.0;
    }

    public int GetEditCount(string skillName)
    {
        return _budgets.TryGetValue(skillName, out var entry) ? entry.EditCount : 0;
    }

    public void ResetBudget(string skillName)
    {
        _budgets[skillName] = new BudgetEntry();
        Save();
    }

    private void Refill(string skillName)
    {
        var entry = _budgets.GetOrAdd(skillName, _ => new BudgetEntry());
        var elapsed = DateTime.UtcNow - entry.LastRefilled;
        if (elapsed.TotalDays >= 1.0)
        {
            var days = elapsed.TotalDays;
            var refill = entry.RefillPerDay * days;
            entry.RemainingRatio = Math.Min(1.0, entry.RemainingRatio + refill);
            entry.LastRefilled = DateTime.UtcNow;
            Save();
        }
    }

    private static int EstimateTokens(string content) =>
        content.Length / 4 + 1;

    private void Load()
    {
        try
        {
            if (File.Exists(_storePath))
            {
                var json = File.ReadAllText(_storePath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, BudgetEntry>>(json);
                if (loaded != null)
                    foreach (var kv in loaded)
                        _budgets[kv.Key] = kv.Value;
            }
        }
        catch { /* best-effort load from persistent store */ }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            lock (_saveLock)
            {
                var snapshot = _budgets.ToDictionary(kv => kv.Key, kv => kv.Value);
                File.WriteAllText(_storePath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch { /* best-effort save to persistent store */ }
    }
}
