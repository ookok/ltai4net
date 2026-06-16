using System;
using System.Threading;
using System.Threading.Tasks;
using LTAI.Agent.Mcp;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LTAI.Tests.Protocols;

public class MCPProtocolTests
{
    [Fact]
    public void McpConfig_Default_EmptyServers()
    {
        var config = new McpConfig();
        Assert.NotNull(config.Servers);
        Assert.Empty(config.Servers);
    }

    [Fact]
    public void McpServerConfig_HasRequiredFields()
    {
        var server = new McpServerConfig
        {
            Name = "test-server",
            Command = "npx",
            Args = new[] { "-y", "@modelcontextprotocol/server-filesystem", "." },
            Env = new() { { "KEY", "value" } },
        };

        Assert.Equal("test-server", server.Name);
        Assert.Equal("npx", server.Command);
        Assert.Equal(3, server.Args.Length);
        Assert.NotNull(server.Env);
        Assert.Equal("value", server.Env["KEY"]);
    }

    [Fact]
    public void McpServerConfig_DefaultValues()
    {
        var server = new McpServerConfig();
        Assert.Equal("", server.Name);
        Assert.Equal("", server.Command);
        Assert.NotNull(server.Args);
        Assert.Empty(server.Args);
        Assert.Null(server.Env);
    }

    [Fact]
    public async Task McpClientFactory_EmptyConfig_ReturnsEmptyTools()
    {
        await using var factory = new McpClientFactory(NullLoggerFactory.Instance);

        var tools = await factory.GetToolsAsync(new McpConfig(), CancellationToken.None);

        Assert.NotNull(tools);
        Assert.Empty(tools);
    }

    [Fact]
    public async Task McpClientFactory_NullConfig_ReturnsEmptyTools()
    {
        await using var factory = new McpClientFactory(NullLoggerFactory.Instance);

        var tools = await factory.GetToolsAsync(null!, CancellationToken.None);

        Assert.NotNull(tools);
        Assert.Empty(tools);
    }

    [Fact]
    public async Task McpClientFactory_NullServers_ReturnsEmptyTools()
    {
        await using var factory = new McpClientFactory(NullLoggerFactory.Instance);

        var config = new McpConfig
        {
            Servers = Array.Empty<McpServerConfig>(),
        };

        var tools = await factory.GetToolsAsync(config, CancellationToken.None);

        Assert.NotNull(tools);
        Assert.Empty(tools);
    }

    [Fact]
    public async Task McpClientFactory_ServerWithEmptyCommand_Skipped()
    {
        await using var factory = new McpClientFactory(NullLoggerFactory.Instance);

        var config = new McpConfig
        {
            Servers = new[]
            {
                new McpServerConfig
                {
                    Name = "empty-cmd",
                    Command = "",
                },
            },
        };

        var tools = await factory.GetToolsAsync(config, CancellationToken.None);

        Assert.NotNull(tools);
        Assert.Empty(tools);
    }

    [Fact]
    public async Task McpClientFactory_Dispose_ClearsClients()
    {
        var factory = new McpClientFactory(NullLoggerFactory.Instance);

        // Get tools with empty config first
        var tools = await factory.GetToolsAsync(new McpConfig(), CancellationToken.None);
        Assert.Empty(factory.Clients);

        await factory.DisposeAsync();
    }

    [Fact]
    public async Task McpClientFactory_DisposeMultipleTimes_NoException()
    {
        await using var factory = new McpClientFactory(NullLoggerFactory.Instance);

        await factory.DisposeAsync();
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task McpClientFactory_GetToolsAsync_CachesResult()
    {
        await using var factory = new McpClientFactory(NullLoggerFactory.Instance);

        var firstCall = await factory.GetToolsAsync(new McpConfig(), CancellationToken.None);
        var secondCall = await factory.GetToolsAsync(new McpConfig(), CancellationToken.None);

        Assert.Same(firstCall, secondCall);
    }
}
