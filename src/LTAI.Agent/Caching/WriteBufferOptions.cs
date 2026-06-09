namespace LTAI.Agent.Caching;

public sealed class WriteBufferOptions
{
    /// <summary>Max time (ms) to hold a dirty entry before flushing.</summary>
    public int FlushIntervalMs { get; init; } = 500;

    /// <summary>Max pending entries before forcing the oldest flush.</summary>
    public int MaxPending { get; init; } = 100;

    /// <summary>Use temp-file + rename for atomic writes.</summary>
    public bool AtomicWrites { get; init; } = true;

    /// <summary>Encoding to use when writing text files.</summary>
    public System.Text.Encoding Encoding { get; init; } = System.Text.Encoding.UTF8;
}
