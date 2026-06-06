namespace LTAI.Core.Configuration;

/// <summary>Configuration for the auto-tuning background service.</summary>
public sealed class AutoTuneConfig
{
    public bool Enabled { get; set; }
    public int Trials { get; set; } = 30;
    public int? Seed { get; set; }
    public string? StorePath { get; set; }
    public string? EvalDir { get; set; }
    public string? ConfigDir { get; set; }
}