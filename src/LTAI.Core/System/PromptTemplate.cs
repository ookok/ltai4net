using System.Text.RegularExpressions;

namespace LTAI.Core.System;

public interface IPromptTemplate
{
    string Render(IReadOnlyDictionary<string, string> variables);
    string Template { get; }
}

public sealed partial class SimplePromptTemplate : IPromptTemplate
{
    public string Template { get; }

    public SimplePromptTemplate(string template)
    {
        Template = template ?? string.Empty;
    }

    public string Render(IReadOnlyDictionary<string, string> variables)
    {
        if (variables.Count == 0) return Template;
        return VariableRegex().Replace(Template, match =>
        {
            var key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return variables.TryGetValue(key, out var value) ? value : match.Value;
        });
    }

    public static string RenderStatic(string template, IReadOnlyDictionary<string, string> variables)
    {
        if (variables.Count == 0) return template;
        return VariableRegex().Replace(template, match =>
        {
            var key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return variables.TryGetValue(key, out var value) ? value : match.Value;
        });
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}|\{(\w+)\}")]
    private static partial Regex VariableRegex();
}
