using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace LTAI.Agent.Tools;

/// <summary>
/// Choice/selection tools: present user with options and await their selection.
/// Ported from DeepSeek-Reasonix choice.ts.
/// </summary>
public static class ChoiceTools
{
    [Description("Present options for the user to choose from")]
    public static string AskChoice(
        [Description("Question to display")] string question,
        [Description("JSON array: [{id, title, summary?}, ...] (2-6 items)")] string optionsJson,
        [Description("Allow user to type custom answer")] bool allowCustom = false)
    {
        ChoiceOption[] options;
        try
        {
            options = JsonSerializer.Deserialize<ChoiceOption[]>(optionsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (JsonException ex)
        {
            return $"Invalid JSON: {ex.Message}";
        }

        if (options.Length < 2 || options.Length > 6)
            return "Please provide 2-6 options";

        var sb = new StringBuilder();
        sb.AppendLine($"## {question}\n");
        sb.AppendLine("| # | Option | Description |");
        sb.AppendLine("|---|--------|-------------|");

        for (int i = 0; i < options.Length; i++)
        {
            var summary = !string.IsNullOrEmpty(options[i].Summary) ? options[i].Summary : "";
            sb.AppendLine($"| {i + 1} | **{options[i].Title}** | {summary} |");
        }

        if (allowCustom)
            sb.AppendLine("\n*Or type your own answer*");

        sb.AppendLine($"\nPlease pick an option (1-{options.Length}){(allowCustom ? " or describe your own" : "")}:");

        return sb.ToString();
    }

    private sealed record ChoiceOption(string Id, string Title, string? Summary = null);
}
