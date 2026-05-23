using LTAI.Agent.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests;

public class EvolutionTests
{
    [Fact]
    public void TC_EVO_01_ToolLifecycle_DetectsFailing()
    {
        var lifecycle = LTAI.Tools.Capability.Governance.ToolLifecycle.Instance;
        lifecycle.Register("vfs:calc:division", "1.0.0");

        for (int i = 0; i < 20; i++)
            lifecycle.RecordInvocation("vfs:calc:division", false);

        var failing = lifecycle.GetFailing(0.3, minInvocations: 10);
        Assert.NotEmpty(failing);
        Assert.Contains(failing, f => f.Name == "vfs:calc:division");
    }

    [Fact]
    public void TC_EVO_02_RollbackHistory_Guards()
    {
        var history = new LTAI.Tools.Evolution.RollbackHistory();
        Assert.False(history.RecordRollback("tool-a"));  // 1st rollback
        Assert.False(history.RecordRollback("tool-a"));  // 2nd rollback
        Assert.True(history.RecordRollback("tool-a"));   // 3rd rollback → freeze
        Assert.Equal(3, history.GetRollbackCount("tool-a"));
    }

    [Fact]
    public void TC_EVO_03_RouterRejectsLowConfidence()
    {
        var router = new IntentRouter();
        var route = router.Classify("xyzzy garbled nonsense unparseable text");
        Assert.True(route.Confidence <= 0.7f);
    }
}
