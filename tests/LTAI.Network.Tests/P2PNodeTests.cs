using LTAI.Core.Configuration;
using LTAI.Infra.Network;
using LTAI.Infra.Network.Discovery;
using LTAI.Infra.Network.Interfaces;
using LTAI.Infra.Network.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LTAI.Network.Tests;

public class P2PNodeTests
{
    private readonly IP2PNode _node;

    public P2PNodeTests()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.Configure<LTAIOptions>(_ => { });
        services.AddSingleton<IP2PNode, P2PNode>();
        var sp = services.BuildServiceProvider();
        _node = sp.GetRequiredService<IP2PNode>();
    }

    [Fact]
    public void PeerId_IsNonEmpty()
    {
        Assert.False(string.IsNullOrEmpty(_node.PeerId));
        Assert.Equal(16, _node.PeerId.Length);
    }

    [Fact]
    public async Task StartAndStop_NoExceptions()
    {
        await _node.StartAsync();
        await _node.StopAsync();
    }

    [Fact]
    public async Task GetKnownPeers_InitiallyEmpty()
    {
        var peers = await _node.GetKnownPeersAsync();
        Assert.Empty(peers);
    }
}

public class ServiceDiscoveryTests
{
    [Fact]
    public void GetLocalPeers_InitiallyEmpty()
    {
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.None));
        var discovery = new ServiceDiscovery(loggerFactory.CreateLogger<ServiceDiscovery>());
        var peers = discovery.GetLocalPeers();
        Assert.Empty(peers);
    }
}
