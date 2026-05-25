using System.Text;
using Microsoft.Extensions.Logging;

namespace LTAI.MCP;

public interface IMCPTransport : IAsyncDisposable
{
    Task StartAsync(MCPServer server, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class StdioTransport : IMCPTransport
{
    private readonly ILogger<StdioTransport> _logger;
    private MCPServer? _server;
    private CancellationTokenSource? _cts;

    public StdioTransport(ILogger<StdioTransport> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(MCPServer server, CancellationToken cancellationToken = default)
    {
        _server = server;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _logger.LogInformation("MCP stdio transport started");

        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        Console.SetOut(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });

        using var reader = new StreamReader(stdin, Encoding.UTF8);
        var writer = new StreamWriter(stdout, Encoding.UTF8) { AutoFlush = true };

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                _logger.LogDebug("MCP <- {Line}", line[..Math.Min(line.Length, 200)]);

                var response = await _server.HandleMessageAsync(line, _cts.Token).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(response))
                {
                    _logger.LogDebug("MCP -> {Response}", response[..Math.Min(response.Length, 200)]);
                    await writer.WriteLineAsync(response).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stdio transport error");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
