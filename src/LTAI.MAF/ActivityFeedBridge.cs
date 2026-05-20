using System.Diagnostics;
using LTAI.Metrics.Monitoring;

namespace LTAI.MAF;

public static class ActivityFeedBridge
{
    private static readonly ActivitySource _source = new("LTAI.ActivityFeed");

    public static void BridgeToOpenTelemetry()
    {
        ActivityFeed.Instance.Value.Subscribe(evt =>
        {
            using var activity = _source.StartActivity($"ltaievent.{evt.Type}");
            activity?.SetTag("ltaievent.type", evt.Type.ToString());
            activity?.SetTag("ltaievent.agent", evt.Agent);
            activity?.SetTag("ltaievent.message", evt.Message);
            activity?.SetTag("ltaievent.severity", evt.Severity.ToString());
            if (evt.Metadata != null)
                foreach (var kv in evt.Metadata)
                    activity?.SetTag($"ltaievent.{kv.Key}", kv.Value?.ToString());
        });
    }
}
