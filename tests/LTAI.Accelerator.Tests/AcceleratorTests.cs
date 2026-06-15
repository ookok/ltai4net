namespace LTAI.Accelerator.Tests;

public sealed class DnsOverHttpsTests
{
    [Fact]
    public void Constructor_CreatesInstance()
    {
        using var doh = new DnsOverHttps();
        Assert.NotNull(doh);
    }

    [Fact]
    public void ClearCache_DoesNotThrow()
    {
        using var doh = new DnsOverHttps();
        var ex = Record.Exception(() => doh.ClearCache());
        Assert.Null(ex);
    }

    [Fact]
    public async Task Resolve_Localhost_ReturnsLoopback()
    {
        using var doh = new DnsOverHttps();
        var addrs = await doh.ResolveAsync("localhost");
        Assert.NotEmpty(addrs);
        Assert.Contains(addrs, a => a.ToString() == "127.0.0.1" || a.ToString() == "::1");
    }

    [Fact]
    public async Task Dispose_CanBeCalledMultipleTimes()
    {
        var doh = new DnsOverHttps();
        doh.Dispose();
        var ex = Record.Exception(() => doh.Dispose());
        Assert.Null(ex);
    }
}

public sealed class ProxyServiceTests
{
    [Fact]
    public void Constructor_WithPort_CreatesInstance()
    {
        using var proxy = new ProxyService(58080);
        Assert.NotNull(proxy);
        Assert.Equal(58080, proxy.Port);
        Assert.False(proxy.IsRunning);
    }

    [Fact]
    public void Constructor_DifferentPorts_Work()
    {
        using var p1 = new ProxyService(58081);
        using var p2 = new ProxyService(58082);
        Assert.Equal(58081, p1.Port);
        Assert.Equal(58082, p2.Port);
    }

    [Fact]
    public async Task StartStop_Lifecycle()
    {
        using var proxy = new ProxyService(58083);
        await proxy.StartAsync();
        Assert.True(proxy.IsRunning);
        await proxy.StopAsync();
        Assert.False(proxy.IsRunning);
    }
}

public sealed class WarpServiceTests
{
    [Fact]
    public void FindWarpCli_ReturnsPathOrNull()
    {
        var path = WarpService.FindWarpCli();
        // Returns path if WARP is installed, null otherwise
        if (path != null)
            Assert.True(File.Exists(path) || path.EndsWith("warp-cli.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var ex = Record.Exception(() => new WarpService());
        Assert.Null(ex);
    }

    [Fact]
    public void Socks5Endpoint_IsLocalhost()
    {
        using var svc = new WarpService();
        Assert.Equal("127.0.0.1:40000", svc.Socks5Endpoint);
    }
}

public sealed class SystemProxyTests
{
    [Fact]
    public void Disable_DoesNotThrow()
    {
        var ex = Record.Exception(() => SystemProxy.Disable());
        Assert.Null(ex);
    }
}
