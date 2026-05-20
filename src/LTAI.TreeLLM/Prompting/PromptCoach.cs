namespace LTAI.TreeLLM.Prompting;

public sealed class PromptCoach
{
    private static readonly Lazy<PromptCoach> _instance = new(() => new PromptCoach());
    public static PromptCoach Instance => _instance.Value;

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

    public void Feedback(string domain, double quality)
    {
        var path = global::System.IO.Path.Combine(".livingtree", "prompts", "coach_feedback.jsonl");
        var entry = System.Text.Json.JsonSerializer.Serialize(new { domain, quality, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        global::System.IO.Directory.CreateDirectory(global::System.IO.Path.GetDirectoryName(path)!);
        global::System.IO.File.AppendAllText(path, entry + "\n");
    }

    public Dictionary<string, string> DomainTemplates => new(_domainTemplates);
}
