using LTAI.Agent.Vector;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Services;

public sealed class GraphInitService : IHostedService
{
    private readonly KbGraph _kbGraph;
    private readonly CgGraph? _cgGraph;
    private readonly ILogger<GraphInitService> _logger;

    public GraphInitService(
        KbGraph kbGraph,
        CgGraph? cgGraph = null,
        ILogger<GraphInitService>? logger = null)
    {
        _kbGraph = kbGraph;
        _cgGraph = cgGraph;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GraphInitService>.Instance;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("GraphInitService: starting background initialization");

        try
        {
            if (_cgGraph != null)
            {
                _logger.LogInformation("GraphInitService: building code graph...");
                var codeResult = await _cgGraph.BuildAsync().ConfigureAwait(false);
                _logger.LogInformation("GraphInitService: code graph done — {Result}",
                    codeResult.Replace("\n", " | "));
            }

            var docDir = Directory.GetCurrentDirectory();
            _logger.LogInformation("GraphInitService: building document index from {Dir}...", docDir);
            var docResult = await _kbGraph.BuildDocumentIndexAsync(docDir).ConfigureAwait(false);
            _logger.LogInformation("GraphInitService: document index done — {Result}", docResult);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GraphInitService: initialization failed (will retry on next /graph init)");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
