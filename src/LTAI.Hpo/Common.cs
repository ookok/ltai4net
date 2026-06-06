namespace LTAI.Hpo;

/// <summary>Result from a single trial evaluation.</summary>
public readonly record struct TrialValue(double Value, int Step);

/// <summary>Direction of optimization.</summary>
public enum StudyDirection { Minimize, Maximize }

/// <summary>State of a trial.</summary>
public enum TrialState { Running, Completed, Pruned, Failed }

/// <summary>Record persisted for each trial.</summary>
public sealed class TrialRecord
{
    public int Number { get; set; }
    public TrialState State { get; set; } = TrialState.Running;
    public double? Value { get; set; }
    public Dictionary<string, object> Params { get; set; } = new();
    public List<TrialValue> IntermediateValues { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}