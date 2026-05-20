using System.Text.Json;
using System.Text.Json.Serialization;
using LTAI.Core.Execution;
using Microsoft.Extensions.Logging;

namespace LTAI.MAF.Evolution;

public sealed class HarnessEdit
{
    [JsonPropertyName("id")] public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("evidence")] public List<string> Evidence { get; init; } = new();
    [JsonPropertyName("root_cause")] public string RootCause { get; init; } = "";
    [JsonPropertyName("fix")] public string Fix { get; init; } = "";

    [JsonPropertyName("component")] public string Component { get; init; } = "";
    [JsonPropertyName("before_hash")] public string BeforeHash { get; init; } = "";
    [JsonPropertyName("after_hash")] public string AfterHash { get; init; } = "";

    [JsonPropertyName("prediction")] public string Prediction { get; init; } = "";
    [JsonPropertyName("predicted_improvement")] public double PredictedImprovement { get; init; }

    [JsonPropertyName("verified")] public bool? Verified { get; set; }
    [JsonPropertyName("verification_result")] public string? VerificationResult { get; set; }
    [JsonPropertyName("actual_improvement")] public double? ActualImprovement { get; set; }
    [JsonPropertyName("verified_at")] public DateTime? VerifiedAt { get; set; }

    [JsonPropertyName("status")] public string Status { get; set; } = "pending";

    public bool IsFalsifiable => !string.IsNullOrWhiteSpace(Prediction) && Status == "pending";
}

public sealed class DecisionLog
{
    private static readonly string LogDir = Path.Combine(".livingtree", "harness", "decisions");
    private static readonly string LogPath = Path.Combine(LogDir, "decision_log.json");

    private readonly List<HarnessEdit> _edits = new();
    private readonly ILogger<DecisionLog>? _logger;
    private readonly object _lock = new();

    public DecisionLog(ILogger<DecisionLog>? logger = null)
    {
        _logger = logger;
        Directory.CreateDirectory(LogDir);
        Load();
    }

    public IReadOnlyList<HarnessEdit> Edits { get { lock (_lock) return _edits.ToList().AsReadOnly(); } }

    public HarnessEdit RecordEdit(
        List<string> evidence,
        string rootCause,
        string fix,
        string component,
        string beforeHash,
        string afterHash,
        string prediction,
        double predictedImprovement)
    {
        var edit = new HarnessEdit
        {
            Evidence = evidence,
            RootCause = rootCause,
            Fix = fix,
            Component = component,
            BeforeHash = beforeHash,
            AfterHash = afterHash,
            Prediction = prediction,
            PredictedImprovement = predictedImprovement,
            Status = "pending"
        };

        lock (_lock) { _edits.Add(edit); Save(); }
        _logger?.LogInformation("Decision recorded: {Id} | {Component} | Predicted +{Improvement:P0} | {RootCause}",
            edit.Id, edit.Component, edit.PredictedImprovement, edit.RootCause[..Math.Min(edit.RootCause.Length, 80)]);

        return edit;
    }

    public HarnessEdit? FindPendingByComponent(string component)
    {
        lock (_lock) { return _edits.FirstOrDefault(e => e.Component == component && e.Status == "pending"); }
    }

    public List<HarnessEdit> GetPendingEdits()
    {
        lock (_lock) { return _edits.Where(e => e.Status == "pending").ToList(); }
    }

    public HarnessEdit? VerifyEdit(string editId, bool predictionHeld, string? result, double actualImprovement)
    {
        lock (_lock)
        {
            var edit = _edits.FirstOrDefault(e => e.Id == editId);
            if (edit == null) return null;

            edit.Verified = predictionHeld;
            edit.VerificationResult = result ?? (predictionHeld ? "Prediction confirmed" : "Prediction failed");
            edit.ActualImprovement = actualImprovement;
            edit.VerifiedAt = DateTime.UtcNow;
            edit.Status = predictionHeld ? "verified" : "falsified";

            Save();
            _logger?.LogInformation("Decision {Id} {Status}: predicted +{Predicted:P0}, actual +{Actual:P0}",
                edit.Id, edit.Status, edit.PredictedImprovement, edit.ActualImprovement);
            return edit;
        }
    }

    public void RollbackEdit(string editId)
    {
        lock (_lock)
        {
            var edit = _edits.FirstOrDefault(e => e.Id == editId);
            if (edit != null && edit.Status != "rolled_back")
            {
                edit.Status = "rolled_back";
                Save();
                _logger?.LogInformation("Decision {Id} rolled back", editId);
            }
        }
    }

    public DecisionStats GetStats()
    {
        lock (_lock)
        {
            return new DecisionStats
            {
                TotalEdits = _edits.Count,
                Pending = _edits.Count(e => e.Status == "pending"),
                Verified = _edits.Count(e => e.Status == "verified"),
                Falsified = _edits.Count(e => e.Status == "falsified"),
                RolledBack = _edits.Count(e => e.Status == "rolled_back"),
                AverageImprovement = _edits.Where(e => e.ActualImprovement.HasValue).Select(e => e.ActualImprovement!.Value).DefaultIfEmpty(0).Average(),
                FalsificationRate = _edits.Count > 0 ? (double)_edits.Count(e => e.Status is "verified" or "falsified" && e.Verified == false) / Math.Max(1, _edits.Count(e => e.Status is "verified" or "falsified")) : 0
            };
        }
    }

    public string? GetLastErrorRate(double currentErrorRate, ref bool improved)
    {
        lock (_lock)
        {
            var last = _edits.LastOrDefault(e => e.Status == "verified" || e.Status == "falsified");
            if (last?.ActualImprovement != null)
            {
                improved = last.ActualImprovement > 0;
                return $"Last edit '{last.Id}': {last.Status}, predicted +{last.PredictedImprovement:P0}, actual +{last.ActualImprovement:P0}";
            }
            improved = false;
            return null;
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_edits, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(LogPath, json);
    }

    private void Load()
    {
        if (!File.Exists(LogPath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<HarnessEdit>>(File.ReadAllText(LogPath));
            if (loaded != null) { lock (_lock) { _edits.Clear(); _edits.AddRange(loaded); } }
        }
        catch { }
    }
}

public sealed class DecisionStats
{
    public int TotalEdits { get; init; }
    public int Pending { get; init; }
    public int Verified { get; init; }
    public int Falsified { get; init; }
    public int RolledBack { get; init; }
    public double AverageImprovement { get; init; }
    public double FalsificationRate { get; init; }
}
