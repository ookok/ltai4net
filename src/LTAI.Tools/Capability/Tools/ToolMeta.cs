using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tools.Tools;

public record DebateResult(string Topic, int Rounds, Dictionary<string, string> Positions,
    string Consensus, Dictionary<string, double> Voting, string Summary);

public record EvolveResult(string ToolName, string OriginalCode, string RewrittenCode,
    string Improvement, bool Applied);

public sealed class ToolMeta
{
    private readonly ILogger<ToolMeta> _logger;

    public ToolMeta(ILogger<ToolMeta>? logger = null)
    {
        _logger = logger ?? NullLogger<ToolMeta>.Instance;
    }

    public async Task<DebateResult> Debate(string topic, List<(string Role, string Position)> roles,
        int maxRounds, Func<string, string, Task<string>> chatFn)
    {
        var positions = new Dictionary<string, string>();
        var voting = new Dictionary<string, double>();
        var rounds = 0;

        foreach (var (role, pos) in roles.Take(8))
        {
            var prompt = $@"You are role-playing as: {role}
Your position on the topic '{topic}' is: {pos}

State your position clearly (2-3 sentences):";
            var response = await chatFn(role, prompt).ConfigureAwait(false);
            positions[role] = response;
        }

        for (rounds = 1; rounds < maxRounds; rounds++)
        {
            var rebuttals = new Dictionary<string, string>();
            foreach (var (role, _) in roles.Take(8))
            {
                var otherPositions = string.Join("\n", positions.Where(p => p.Key != role).Select(p => $"{p.Key}: {p.Value}"));
                var prompt = $@"You are role-playing as: {role}
Other positions:\n{otherPositions}

Provide your rebuttal (2-3 sentences):";
                var response = await chatFn($"{role}_r{rounds}", prompt);
                rebuttals[role] = response;
            }

            var converged = true;
            foreach (var (role, rebuttal) in rebuttals)
            {
                if (rebuttal.Contains(positions[role]) && rebuttal.Length < positions[role].Length * 1.5)
                    continue;
                converged = false;
                positions[role] = rebuttal;
            }
            if (converged) break;
        }

        foreach (var (role, _) in roles.Take(8))
            voting[role] = Math.Round(0.5 + (positions.Count % 3) * 0.15, 2);

        var consensusPrompt = $@"Based on this debate about '{topic}', identify the consensus:

{string.Join("\n\n", positions.Select(p => $"{p.Key}: {p.Value}"))}

Return JSON: {{""consensus"": ""..."" , ""summary"": ""...""}}";
        var consensusResponse = await chatFn("consensus", consensusPrompt);

        string consensus = "No consensus reached";
        string summary = "Debate completed";
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(ExtractJson(consensusResponse));
            consensus = json.TryGetProperty("consensus", out var c) ? c.GetString() ?? consensus : consensus;
            summary = json.TryGetProperty("summary", out var s) ? s.GetString() ?? summary : summary;
        }
        catch { /* non-fatal */ }

        return new DebateResult(topic, rounds, positions, consensus, voting, summary);
    }

    public async Task<EvolveResult> SelfEvolve(string toolName, string originalCode,
        List<string> errorLog, Func<string, string, Task<string>> chatFn)
    {
        var prompt = $@"You are a code reviewer. The tool '{toolName}' has been failing.

Original code:
{originalCode}

Error log:
{string.Join("\n", errorLog.TakeLast(10))}

Propose a fix. Return JSON: {{""improvement"": ""..."" , ""fixed_code"": ""...""}}";
        var response = await chatFn($"fix_{toolName}", prompt);
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(ExtractJson(response));
            var improvement = json.TryGetProperty("improvement", out var imp) ? imp.GetString() ?? "" : "";
            var fixedCode = json.TryGetProperty("fixed_code", out var fc) ? fc.GetString() ?? originalCode : originalCode;

            var hotfixDir = Path.Combine(Environment.GetEnvironmentVariable("LTAI_WORKSPACE") ?? Directory.GetCurrentDirectory(), ".livingtree", "hotfixes");
            Directory.CreateDirectory(hotfixDir);
            var hotfixPath = Path.Combine(hotfixDir, $"{SantizeFileName(toolName)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            File.WriteAllText(hotfixPath, JsonSerializer.Serialize(new { toolName, originalCode, fixedCode, improvement },
                new JsonSerializerOptions { WriteIndented = true }));

            return new EvolveResult(toolName, originalCode, fixedCode, improvement, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to evolve tool: {ToolName}", toolName);
            return new EvolveResult(toolName, originalCode, originalCode, "Failed to parse fix", false);
        }
    }

    private static string ExtractJson(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```")) text = text[(text.IndexOf('\n') + 1)..];
        if (text.EndsWith("```")) text = text[..text.LastIndexOf("```")];
        return text;
    }

    private static string SantizeFileName(string name)
        => string.Join("_", name.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_").ToLowerInvariant();
}
