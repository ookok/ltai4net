namespace LTAI.Core.I18n;

/// <summary>
/// D2: Resolves {{locale.XXX}} template variables in YAML workflow content.
/// Called by <c>YAMLWorkflowRegistry</c> at load time to patch greeting
/// strings before the workflow is compiled by MAF.
///
/// Usage in YAML:
/// <code>
/// value: "{{locale.GreetingHello}}"
/// </code>
/// Gets replaced with the current locale's greeting at workflow load time.
/// </summary>
public static class WorkflowI18nResolver
{
    private static readonly System.Text.RegularExpressions.Regex TemplateRx =
        new(@"\{\{locale\.(\w+)\}\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Replace all {{locale.Key}} placeholders with localized strings.</summary>
    public static string Resolve(string yamlContent)
    {
        return TemplateRx.Replace(yamlContent, match =>
        {
            var key = match.Groups[1].Value;
            return Locale.Get(key);
        });
    }
}
