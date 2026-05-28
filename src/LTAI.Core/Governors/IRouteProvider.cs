namespace LTAI.Core.Governors;

/// <summary>
/// Provides a route strategy with Quality/Speed/Cost metrics for the ParetoRouter.
/// External extensions can register additional routes (e.g., L3, L0.5) at startup.
/// </summary>
public interface IRouteProvider
{
    string Label { get; }
    float Quality { get; }
    float Speed { get; }
    float Cost { get; }
}

/// <summary>Default route providers — reflex, local, L1, L2.</summary>
public sealed record DefaultRouteProvider(string Label, float Quality, float Speed, float Cost) : IRouteProvider;
