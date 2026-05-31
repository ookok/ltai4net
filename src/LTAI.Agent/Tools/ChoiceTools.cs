using System.ComponentModel;
using System.Text;
using System.Text.Json;
using LTAI.AI;

namespace LTAI.Agent.Tools;

/// <summary>
/// Choice/selection tools: present user with options and await their selection.
/// Ported from DeepSeek-Reasonix choice.ts.
/// </summary>
[ToolDomain("choice")]
public static class ChoiceTools
{
    [Description("向用户展示多个选项供选择。用于需要用户做决策的场景。\n"
        + "适用场景：让用户从多个方案中选择一个、确认操作方向、用户需要在 A/B/C 之间做决定。\n"
        + "不适用场景：只需 yes/no 确认（请用工具确认机制）、展示信息不需要选择。\n"
        + "关键参数：question — 展示给用户的问题；optionsJson — JSON 选项数组(2-6项)；allowCustom — 是否允许用户自定义答案。")]
    [ToolExample("你想怎么处理这个文件？")]
    [ToolExample("选一个方案继续")]
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
