using System.Text.Json;
using Spectre.Console;

namespace LTAI.TUI;

public sealed class PromptLibrary
{
    private readonly string _storePath;
    private readonly Lock _templatesLock = new();
    private List<PromptTemplate> _templates = new();
    private Dictionary<string, DiagnosticTemplate> _diagnosticTemplates = new();

    public PromptLibrary(string projectRoot)
    {
        _storePath = Path.Combine(projectRoot, ".livingtree", "prompts.json");
        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_storePath))
            {
                var loaded = JsonSerializer.Deserialize<List<PromptTemplate>>(File.ReadAllText(_storePath)) ?? new();
                lock (_templatesLock) { _templates = loaded; }
            }
        }
        catch { lock (_templatesLock) { _templates = GetBuiltInTemplates(); } }
        if (_templates.Count == 0) lock (_templatesLock) { _templates = GetBuiltInTemplates(); }

        lock (_templatesLock) { _diagnosticTemplates = GetBuiltInDiagnosticTemplates(); }
    }

    public DiagnosticTemplate? GetDiagnosticTemplate(string code)
    {
        _templatesLock.Enter();
        try { return _diagnosticTemplates.GetValueOrDefault(code); }
        finally { _templatesLock.Exit(); }
    }

    private void Save()
    {
        List<PromptTemplate> snapshot;
        lock (_templatesLock) { snapshot = _templates.ToList(); }
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        File.WriteAllText(_storePath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    }

    public string? SelectPrompt()
    {
        List<PromptTemplate> snapshot;
        lock (_templatesLock) { snapshot = _templates.ToList(); }
        if (snapshot.Count == 0) return null;

        var selected = AnsiConsole.Prompt(new SelectionPrompt<PromptTemplate>()
            .Title("[cyan]Select a prompt template:[/]")
            .PageSize(12)
            .AddChoices(snapshot)
            .UseConverter(t => $"{t.Category switch { "code" => "🔧", "review" => "🔍", "test" => "🧪", "doc" => "📝", "ask" => "💬", _ => "📄" }} [grey]{t.Category}[/] {t.Name}"));

        var resolved = selected.Template;
        var variables = ExtractVariables(resolved);
        foreach (var v in variables)
        {
            var value = AnsiConsole.Ask<string>($"[grey]  {v}:[/] ", string.Empty);
            resolved = resolved.Replace($"{{{v}}}", value);
        }

        if (selected.Category == "code")
        {
            if (resolved.Contains("{file}") || resolved.Contains("{language}"))
            {
                var path = AnsiConsole.Ask<string>("[grey]  file (@ for none):[/] ", "");
                if (!string.IsNullOrWhiteSpace(path) && path != "@")
                    resolved = resolved.Replace("{file}", File.Exists(path) ? File.ReadAllText(path)[..Math.Min(new FileInfo(path).Length, 3000).ToInt32()] : path);
            }
        }

        return resolved;
    }

    public async Task AddTemplateAsync()
    {
        var category = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("[cyan]Category:[/]")
            .AddChoices("code", "review", "test", "doc", "ask", "custom"));

        var name = AnsiConsole.Ask<string>("[cyan]Name:[/] ", "");
        if (string.IsNullOrWhiteSpace(name)) return;

        AnsiConsole.MarkupLine("[grey]Enter template (use {file} {language} {context} as variables). Press Enter twice to finish.[/]");
        var lines = new List<string>();
        string? line;
        while (!string.IsNullOrWhiteSpace(line = Console.ReadLine()) || lines.Count == 0)
        {
            if (line == null) break;
            lines.Add(line);
        }

        var template = new PromptTemplate { Name = name, Template = string.Join("\n", lines), Category = category };
        lock (_templatesLock) { _templates.Add(template); }
        Save();
        AnsiConsole.MarkupLine($"[green]Saved:[/] {name}");
        await Task.CompletedTask;
    }

    private static List<string> ExtractVariables(string template)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(template, @"\{(\w+)}");
        return matches.Select(m => m.Groups[1].Value).Distinct().ToList();
    }

    private static List<PromptTemplate> GetBuiltInTemplates() => new()
    {
        new() { Name = "Code Review", Category = "review", Template = "Review the following {language} code for bugs, performance issues, and style problems:\n```{language}\n{file}\n```\nProvide specific suggestions with line references." },
        new() { Name = "Refactor", Category = "code", Template = "Refactor this {language} code to improve readability and maintainability while preserving functionality:\n```{language}\n{file}\n```\nExplain each change." },
        new() { Name = "Generate Tests", Category = "test", Template = "Generate comprehensive unit tests for this {language} code using {framework}:\n```{language}\n{file}\n```\nCover edge cases, error handling, and happy paths." },
        new() { Name = "Document Function", Category = "doc", Template = "Write clear documentation for this function. Include purpose, parameters, return value, exceptions, and usage examples:\n```{language}\n{file}\n```" },
        new() { Name = "Explain Code", Category = "ask", Template = "Explain what this code does in detail, suitable for a developer new to the codebase:\n```{language}\n{file}\n```" },
        new() { Name = "Find Bugs", Category = "review", Template = "Analyze this code for potential bugs, security vulnerabilities, and race conditions:\n```{language}\n{file}\n```" },
        new() { Name = "Optimize Performance", Category = "code", Template = "Identify performance bottlenecks in this code and suggest optimizations:\n```{language}\n{file}\n```" },
        new() { Name = "Add Error Handling", Category = "code", Template = "Add proper error handling, logging, and validation to this code:\n```{language}\n{file}\n```" },
    };

    private static Dictionary<string, DiagnosticTemplate> GetBuiltInDiagnosticTemplates() => new()
    {
        ["CS1061"] = new() { Code = "CS1061", Title = "Missing member", FixPattern = "Verify the member name exists on the target type. Check for missing using directives or extension methods." },
        ["CS8602"] = new() { Code = "CS8602", Title = "Possible null dereference", FixPattern = "Add null check or null-forgiving operator. Verify the variable is initialized before use." },
        ["CS0103"] = new() { Code = "CS0103", Title = "Name not found", FixPattern = "Check for missing using directive, variable scope, or typo in the identifier name." },
        ["CS0117"] = new() { Code = "CS0117", Title = "Member not accessible", FixPattern = "Verify type compatibility. The member may belong to a different type or require a using directive for extension methods." },
        ["CS0246"] = new() { Code = "CS0246", Title = "Type not found", FixPattern = "Add missing using directive or project reference. Check namespace qualification." },
        ["CS1503"] = new() { Code = "CS1503", Title = "Type mismatch", FixPattern = "Add type cast or conversion. Verify overload signatures." },
        ["CS0165"] = new() { Code = "CS0165", Title = "Unassigned variable", FixPattern = "Initialize the variable before use. Consider using 'default' or 'new'." },
        ["NullReference"] = new() { Code = "NullReference", Title = "Null reference", FixPattern = "Add null check with 'if (x is null) return' or 'ArgumentNullException.ThrowIfNull(x)'. Verify initialization order." },
        ["ArgumentNull"] = new() { Code = "ArgumentNull", Title = "Null argument", FixPattern = "Add guard clause at method entry: ArgumentNullException.ThrowIfNull(paramName)." },
        ["FileNotFound"] = new() { Code = "FileNotFound", Title = "Missing file", FixPattern = "Check file path. Create directory if needed with Directory.CreateDirectory(Path.GetDirectoryName(path)!)." },
    };
}

public sealed class PromptTemplate
{
    public string Name { get; init; } = "";
    public string Template { get; init; } = "";
    public string Category { get; init; } = "custom";
}

public sealed class DiagnosticTemplate
{
    public string Code { get; init; } = "";
    public string Title { get; init; } = "";
    public string FixPattern { get; init; } = "";
}

internal static class Int32Extensions
{
    public static int ToInt32(this long value) => (int)Math.Min(value, int.MaxValue);
}
