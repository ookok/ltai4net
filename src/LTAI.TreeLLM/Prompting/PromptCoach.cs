using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.TreeLLM.Prompting;

public sealed record CoachResult
{
    public string MetaPrompt { get; init; } = "";
    public string PreAnalysis { get; init; } = "";
    public int Complexity { get; init; } = 5;
    public bool Parsed { get; init; }
}

public sealed class PromptCoach
{
    private static readonly Lazy<PromptCoach> _instance = new(() => new PromptCoach());
    public static PromptCoach Instance => _instance.Value;

    private static readonly Lock _feedbackLock = new();

    private readonly Dictionary<string, string> _domainTemplates = new()
    {
        ["code"] = "Structure the code generation: 1) Requirements analysis 2) Architecture overview 3) Implementation with tests 4) Edge case handling",
        ["analysis"] = "Structure the analysis: 1) Context summary 2) Key findings 3) Evidence and data 4) Conclusions and recommendations",
        ["creative"] = "Structure creative output: 1) Concept overview 2) Detailed elaboration 3) Alternative perspectives 4) Refinement",
        ["general"] = "Structure the response: 1) Direct answer 2) Supporting details 3) Examples or evidence 4) Summary"
    };

    private PromptCoach() { }

    public string Coach(string userInput, string domain = "general")
    {
        var template = _domainTemplates.GetValueOrDefault(domain, _domainTemplates["general"]);
        return $"You are a prompt engineering coach. Optimize the following query for an AI reasoning model.\n\n" +
               $"Task domain: {domain}\nStructure guideline:\n{template}\n\n" +
               $"User query: {userInput}\n\n" +
               $"Generate a JSON with: meta_prompt (optimized query), pre_analysis (context notes), complexity (1-10)";
    }

    public string CoachedChat(string userInput, string domain = "general")
    {
        var coachPrompt = Coach(userInput, domain);
        return $"<coach>\nCoach analysis: {coachPrompt}\n</coach>\n\n<task>\n{userInput}\n</task>";
    }

    public static CoachResult ParseCoachResult(string llmResponse)
    {
        try
        {
            var json = ExtractJsonBlock(llmResponse);
            if (string.IsNullOrEmpty(json)) return new CoachResult { Parsed = false };

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var metaPrompt = string.Empty;
            var preAnalysis = string.Empty;
            var complexity = 5;

            if (root.TryGetProperty("meta_prompt", out var mp))
                metaPrompt = mp.GetString() ?? "";
            if (root.TryGetProperty("pre_analysis", out var pa))
                preAnalysis = pa.GetString() ?? "";
            if (root.TryGetProperty("complexity", out var cx) && cx.TryGetInt32(out var c))
                complexity = Math.Clamp(c, 1, 10);

            return new CoachResult
            {
                MetaPrompt = metaPrompt,
                PreAnalysis = preAnalysis,
                Complexity = complexity,
                Parsed = true
            };
        }
        catch
        {
            return new CoachResult { Parsed = false };
        }
    }

    private static string ExtractJsonBlock(string text)
    {
        var match = Regex.Match(text, @"```(?:json)?\s*\n?(.*?)\n?```", RegexOptions.Singleline);
        if (match.Success) return match.Groups[1].Value.Trim();

        var braceStart = text.IndexOf('{');
        var braceEnd = text.LastIndexOf('}');
        if (braceStart >= 0 && braceEnd > braceStart)
            return text[braceStart..(braceEnd + 1)];

        return string.Empty;
    }

    public void Feedback(string domain, double quality)
    {
        lock (_feedbackLock)
        {
            var path = global::System.IO.Path.Combine(".livingtree", "prompts", "coach_feedback.jsonl");
            var entry = System.Text.Json.JsonSerializer.Serialize(new { domain, quality, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            global::System.IO.Directory.CreateDirectory(global::System.IO.Path.GetDirectoryName(path)!);
            global::System.IO.File.AppendAllText(path, entry + "\n");
        }
    }

    public Dictionary<string, string> DomainTemplates => new(_domainTemplates);
}
