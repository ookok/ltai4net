// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  McpClientFactory — Connect to external MCP servers (stdio) and
//  expose their tools as AIFunctions to the LTAI agent.
//
//  On startup, reads opts.Mcp.Servers. For each entry, spawns the
//  configured process (e.g. npx @modelcontextprotocol/server-filesystem),
//  negotiates the MCP protocol, lists available tools, and returns
//  them as a flat list of AIFunction. The returned object also tracks
//  the underlying McpClient instances for cleanup on shutdown.
//
//  Each MCP server runs as a child process; failures to connect are
//  logged and skipped (the agent continues without those tools).
// ═══════════════════════════════════════════════════════════════

using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace LTAI.Agent.Mcp;

public sealed class McpClientFactory : IAsyncDisposable
{
    private readonly List<McpClient> _clients = new();
    private readonly ILogger<McpClientFactory>? _logger;
    private List<AITool>? _cachedTools;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public IReadOnlyList<McpClient> Clients => _clients;

    public McpClientFactory(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<McpClientFactory>();
    }

    /// <summary>
    /// Get the aggregated list of tools from all connected MCP servers.
    /// First call triggers <see cref="ConnectAsync"/>; subsequent calls return
    /// the cached list. Thread-safe.
    /// </summary>
    public async Task<IReadOnlyList<AITool>> GetToolsAsync(McpConfig config, CancellationToken ct = default)
    {
        if (_cachedTools != null) return _cachedTools;
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedTools != null) return _cachedTools;
            var tools = await ConnectAsync(config, ct).ConfigureAwait(false);
            _cachedTools = tools;
            return tools;
        }
        finally { _initLock.Release(); }
    }

    /// <summary>
    /// Connect to all configured MCP servers, list their tools, and return
    /// them as a flat list of <see cref="AIFunction"/>. Failures are logged
    /// and the affected server is skipped.
    /// </summary>
    public async Task<List<AITool>> ConnectAsync(McpConfig config, CancellationToken ct = default)
    {
        var tools = new List<AITool>();
        if (config?.Servers is not { Length: > 0 }) return tools;

        foreach (var server in config.Servers)
        {
            if (string.IsNullOrWhiteSpace(server.Command))
            {
                _logger?.LogWarning("MCP server '{Name}' has empty Command — skipped", server.Name);
                continue;
            }

            try
            {
                var transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = server.Name,
                    Command = server.Command,
                    Arguments = server.Args?.ToList() ?? new List<string>(),
                });

                var client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
                _clients.Add(client);

                var serverTools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
                _logger?.LogInformation(
                    "McpClientFactory: connected to '{Name}', {Count} tools available",
                    server.Name, serverTools.Count);

                foreach (var tool in serverTools)
                    tools.Add(tool);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "McpClientFactory: failed to connect to MCP server '{Name}' — skipped", server.Name);
            }
        }

        return tools;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger?.LogWarning(ex, "McpClientFactory: dispose failed"); }
        }
        _clients.Clear();
        _cachedTools = null;
    }
}
