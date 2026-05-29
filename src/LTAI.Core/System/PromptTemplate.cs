using System.Text;
using System.Text.RegularExpressions;

namespace LTAI.Core.System;

/// <summary>
/// A simple string template with variable substitution and block helpers.
///
/// Supported syntax:
///   - Variables: {{varName}} or {varName}
///   - Conditionals: {{#if varName}}...{{/if}}
///   - Negation: {{#unless varName}}...{{/unless}}
///   - Loops: {{#each varName}}...{{/each}} — iterates over newline-separated values
///   - Arrays in loops: {{#each varName}}...{{/each}} — iterates over items[] array values
///
/// Block helpers are nestable but NOT self-closing (each must have a matching {{/...}}).
///
/// Callers: LTAI.Tools.DocEngine, LTAI.Knowledge.Core.PromptService,
///          LTAI.Agent.MAF.SystemPromptAssembler.
/// </summary>
public interface IPromptTemplate
{
    /// <summary>Render the template with the given variables.</summary>
    string Render(IReadOnlyDictionary<string, string> variables);

    /// <summary>The raw template string.</summary>
    string Template { get; }
}

/// <summary>
/// Flexible prompt template with variable substitution, conditionals, and loops.
/// Replaces {{var}} and {var} placeholders. Supports {{#if}}/{{#unless}} blocks
/// and {{#each}} loops. Fallback rendering ensures unmatched control tags
/// are left as-is (degrading gracefully) rather than throwing.
/// </summary>
public sealed partial class SimplePromptTemplate : IPromptTemplate
{
    private readonly string _template;
    private static readonly char[] Newlines = ['\n', '\r'];

    public string Template => _template;

    public SimplePromptTemplate(string template)
    {
        _template = template ?? string.Empty;
    }

    /// <summary>
    /// Render the template with the given variables.
    /// Supports:
    ///   {{key}} / {key} → variable substitution
    ///   {{#if key}}...{{/if}} → include block if key is truthy (non-empty, not "false", not "0")
    ///   {{#unless key}}...{{/unless}} → include block if key is falsy
    ///   {{#each key}}...{{/each}} → repeat block for each item (items separated by newline or in items[] sub-dict)
    /// </summary>
    public string Render(IReadOnlyDictionary<string, string> variables)
    {
        if (variables.Count == 0)
            return StripControlBlocks(_template);

        return RenderBlock(_template, variables);
    }

    /// <summary>Static convenience method for one-off rendering.</summary>
    public static string RenderStatic(string template, IReadOnlyDictionary<string, string> variables)
        => new SimplePromptTemplate(template).Render(variables);

    // ========================================================================
    // Internal recursive block renderer
    // ========================================================================

    private static string RenderBlock(string text, IReadOnlyDictionary<string, string> variables)
    {
        // 1. Variable substitution
        text = VariableRegex().Replace(text, match =>
        {
            var key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return variables.TryGetValue(key, out var value) ? value : match.Value;
        });

        // 2. {{#each}} blocks (must be processed before {{#if}} to allow nesting)
        text = ProcessEachBlocks(text, variables);

        // 3. {{#if}} blocks
        text = ProcessIfBlocks(text, variables, negate: false);

        // 4. {{#unless}} blocks
        text = ProcessIfBlocks(text, variables, negate: true);

        // 5. Clean up any remaining unmatched control tags
        text = StripControlBlocks(text);

        return text;
    }

    private static string ProcessIfBlocks(string text, IReadOnlyDictionary<string, string> variables, bool negate)
    {
        var tag = negate ? "unless" : "if";
        var pattern = $@"{{{{\#({tag})\s+(\w+)}}}}(.*?){{{{\/{tag}}}}}";

        return IfBlockRegex().Replace(text, match =>
        {
            var key = match.Groups[2].Value;
            var body = match.Groups[3].Value;

            var isTruthy = IsTruthy(variables, key);
            var shouldInclude = negate ? !isTruthy : isTruthy;

            if (!shouldInclude)
                return "";

            // Recursively render nested blocks in the body
            return RenderBlock(body, variables);
        });
    }

    private static string ProcessEachBlocks(string text, IReadOnlyDictionary<string, string> variables)
    {
        return EachBlockRegex().Replace(text, match =>
        {
            var key = match.Groups[1].Value;
            var body = match.Groups[2].Value;

            // Resolve the "items" from variables
            var items = GetItems(key, variables);
            if (items.Count == 0)
                return "";

            var sb = new StringBuilder();
            var itemIndex = 0;
            foreach (var item in items)
            {
                // Build item-specific variables
                var itemVars = new Dictionary<string, string>(variables)
                {
                    ["this"] = item,
                    ["index"] = itemIndex.ToString()
                };
                sb.Append(RenderBlock(body, itemVars));
                itemIndex++;
            }
            return sb.ToString();
        });
    }

    private static List<string> GetItems(string key, IReadOnlyDictionary<string, string> variables)
    {
        // First try: direct value (newline-separated list)
        if (variables.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
        {
            return value.Split(Newlines, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
        }

        // Second try: items_{key} array pattern (for structured data)
        var items = new List<string>();
        for (var i = 0; ; i++)
        {
            var itemKey = $"items_{key}_{i}";
            if (variables.TryGetValue(itemKey, out var item))
                items.Add(item);
            else
                break;
        }

        return items;
    }

    private static bool IsTruthy(IReadOnlyDictionary<string, string> variables, string key)
    {
        if (!variables.TryGetValue(key, out var value))
            return false;

        var trimmed = value.Trim().ToLowerInvariant();
        return trimmed.Length > 0
            && trimmed != "false"
            && trimmed != "0"
            && trimmed != "no"
            && trimmed != "none";
    }

    /// <summary>Remove any remaining unmatched {{#...}} / {{/...}} control tags.</summary>
    private static string StripControlBlocks(string text)
    {
        return StripControlRegex().Replace(text, "");
    }

    // ========================================================================
    // Compiled regex patterns
    // ========================================================================

    [GeneratedRegex(@"\{\{(\w+)\}\}|\{(\w+)\}")]
    private static partial Regex VariableRegex();

    [GeneratedRegex(@"\{\{#(if|unless)\s+(\w+)\}\}(.*?)\{\{/(if|unless)\}\}", RegexOptions.Singleline)]
    private static partial Regex IfBlockRegex();

    [GeneratedRegex(@"\{\{#each\s+(\w+)\}\}(.*?)\{\{/each\}\}", RegexOptions.Singleline)]
    private static partial Regex EachBlockRegex();

    [GeneratedRegex(@"\{\{/?[a-z]+\s*\w*\}\}", RegexOptions.Singleline)]
    private static partial Regex StripControlRegex();
}
