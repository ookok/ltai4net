namespace LTAI.Core.Configuration;

/// <summary>
/// Configuration for tool trust — tools listed here execute without user confirmation.
/// Configured in appsettings.json under LTAI:Tools:TrustedToolNames.
/// Supports glob-style wildcards: "SafeShellTool.*", "*.RunCommand"
/// </summary>
public sealed class ToolTrustConfig
{
    /// <summary>List of trusted tool names/patterns (glob-style).</summary>
    public string[] TrustedToolNames { get; init; } = [];

    /// <summary>
    /// If true, ALL tools are trusted (dangerous — overrides TrustedToolNames).
    /// </summary>
    public bool TrustAll { get; init; } = false;
}
