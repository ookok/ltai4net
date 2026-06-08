using LTAI.Agent.Vector;
using LTAI.Core.Commands;

namespace LTAI.TUI.Services;

public sealed class GraphCommandService : ICommandService
{
    private readonly CgGraph? _cgGraph;
    private readonly KbGraph? _kbGraph;

    public GraphCommandService(CgGraph? cgGraph, KbGraph? kbGraph)
    {
        _cgGraph = cgGraph;
        _kbGraph = kbGraph;
    }

    public Task<CommandResult> ExecuteAsync(Command command) => command switch
    {
        GraphCommand gc => Task.FromResult(HandleGraph(gc.Args)),
        _ => Task.FromResult<CommandResult>(new SuccessResult("ok")),
    };

    private CommandResult HandleGraph(string args)
    {
        if (string.IsNullOrWhiteSpace(args) || args == "init")
        {
            if (_cgGraph == null)
                return new SuccessResult("CodeGraph not available");

            _ = BuildGraphAsync();
            return new SuccessResult("Building code graph + document index...");
        }

        if (args.StartsWith("search", StringComparison.OrdinalIgnoreCase))
        {
            if (_cgGraph == null)
                return new SuccessResult("CodeGraph not available");

            var query = args.Length > 7 ? args[7..].Trim() : "";
            if (string.IsNullOrWhiteSpace(query))
                return new SuccessResult("Usage: /graph search <query>");

            _ = SearchGraphAsync(query);
            return new SuccessResult("Searching graph...");
        }

        return new SuccessResult("Usage: /graph init|search <query>");
    }

    private async Task BuildGraphAsync()
    {
        try
        {
            var codeResult = await _cgGraph!.BuildAsync().ConfigureAwait(false);
            var docResult = "";
            if (_kbGraph != null)
                docResult = await _kbGraph.BuildDocumentIndexAsync(Directory.GetCurrentDirectory()).ConfigureAwait(false);
            SlashCommands.PendingBuildResult = $"Code: {codeResult.Replace("\n", " | ")}\nDocs: {docResult}";
        }
        catch (Exception ex)
        {
            SlashCommands.PendingBuildResult = $"Error: {ex.Message}";
        }
    }

    private async Task SearchGraphAsync(string query)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            var codeResult = await _cgGraph!.QueryAsync(query, topK: 3).ConfigureAwait(false);
            if (!codeResult.StartsWith("No relevant") && !codeResult.StartsWith("Code graph not built"))
                sb.AppendLine(codeResult);
            if (_kbGraph != null)
            {
                try
                {
                    var kbResults = await _kbGraph.QueryAsync(query, topK: 5).ConfigureAwait(false);
                    if (kbResults.Count > 0)
                        sb.AppendLine("## Relevant Knowledge:\n" + string.Join("\n", kbResults.Select(r => "- " + r)));
                }
                catch { }
            }
            SlashCommands.PendingBuildResult = sb.Length > 0
                ? sb.ToString().Replace("\n", " | ")
                : "No results found.";
        }
        catch (Exception ex)
        {
            SlashCommands.PendingBuildResult = $"Error: {ex.Message}";
        }
    }
}
