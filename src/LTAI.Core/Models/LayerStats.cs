namespace LTAI.Core.Models;

public sealed class LayerStats
{
    public string LayerName { get; init; } = string.Empty;
    public long MessagesSent { get; set; }
    public long MessagesReceived { get; set; }
    public long Errors { get; set; }
    public double AvgLatencyMs { get; set; }
    public DateTime LastActive { get; set; } = DateTime.UtcNow;
}
