using System.ComponentModel;
using System.Text.Json;

namespace LTAI.MAF.Tools;

[Description("Code analysis, snippet generation, and language detection tools")]
public sealed class CodeTools
{
    [Description("Analyze a code snippet: count lines, detect language, identify functions and classes.")]
    public static string AnalyzeCode(
        [Description("Source code text")] string code,
        [Description("Programming language hint: csharp, python, javascript, typescript, java, go, rust, sql, html, css")] string? language = null)
    {
        var lines = code.Split('\n');
        var codeLines = lines.Count(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("//") && !l.TrimStart().StartsWith("#"));
        var blankLines = lines.Count(string.IsNullOrWhiteSpace);
        var commentLines = lines.Length - codeLines - blankLines;

        var detectedLang = language ?? DetectLanguage(code);

        var functions = System.Text.RegularExpressions.Regex.Matches(code, @"(?:def|function|func|fn|void|public|private|protected|async)\s+\w+\s*\(").Count;
        var classes = System.Text.RegularExpressions.Regex.Matches(code, @"\bclass\s+\w+").Count;
        var imports = System.Text.RegularExpressions.Regex.Matches(code, @"(?:import|using|require|from)\s+[\w.]+").Select(m => m.Value).Distinct().Take(20).ToList();

        return JsonSerializer.Serialize(new
        {
            language = detectedLang,
            totalLines = lines.Length,
            codeLines,
            blankLines,
            commentLines,
            functions,
            classes,
            imports,
            charCount = code.Length,
            averageLineLength = codeLines > 0 ? code.Length / codeLines : 0
        });
    }

    [Description("Generate a code snippet for common patterns: rest-client, db-query, file-io, sort, filter, map.")]
    public static string GenerateSnippet(
        [Description("Pattern type: rest-client, db-query, file-io, sort, filter, map, regex, json-parse, csv-read, env-read")] string pattern,
        [Description("Target language: csharp, python, javascript, typescript, go")] string language = "csharp")
    {
        var (lang, snippet) = GetSnippet(pattern, language);
        return JsonSerializer.Serialize(new { pattern, language = lang, snippet });
    }

    [Description("Convert code between JSON objects and C#/Python/TypeScript class definitions.")]
    public static string JsonToClass(
        [Description("JSON string to generate class from")] string json,
        [Description("Target language: csharp, python, typescript")] string language = "csharp",
        [Description("Root class name")] string className = "Root")
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var code = GenerateClass(doc.RootElement, className, language.ToLowerInvariant());
            return JsonSerializer.Serialize(new { language, className, code });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private static string GenerateClass(JsonElement element, string name, string lang)
    {
        var props = new List<(string Name, string Type)>();
        foreach (var prop in element.EnumerateObject())
        {
            var type = GetJsonType(prop.Value, lang);
            var propName = lang switch
            {
                "csharp" => char.ToUpperInvariant(prop.Name[0]) + prop.Name[1..],
                "python" => prop.Name.ToLowerInvariant(),
                _ => prop.Name
            };
            props.Add((propName, type));
        }

        return lang switch
        {
            "csharp" => $"public class {name}\n{{\n" + string.Join("\n", props.Select(p => $"    public {p.Type} {p.Name} {{ get; set; }}")) + "\n}",
            "python" => $"@dataclass\nclass {name}:\n" + string.Join("\n", props.Select(p => $"    {p.Name}: {p.Type}")),
            "typescript" => $"interface {name} {{\n" + string.Join("\n", props.Select(p => $"    {p.Name}: {p.Type};")) + "\n}",
            _ => $"class {name} {{ ... }}"
        };
    }

    private static string GetJsonType(JsonElement e, string lang) => e.ValueKind switch
    {
        JsonValueKind.String => lang == "python" ? "str" : lang == "typescript" ? "string" : "string",
        JsonValueKind.Number => e.GetRawText().Contains('.') ? (lang == "python" ? "float" : lang == "typescript" ? "number" : "double") : (lang == "python" ? "int" : lang == "typescript" ? "number" : "int"),
        JsonValueKind.True or JsonValueKind.False => lang == "python" ? "bool" : lang == "typescript" ? "boolean" : "bool",
        JsonValueKind.Array => lang == "python" ? "list" : lang == "typescript" ? "any[]" : "List<object>",
        JsonValueKind.Object => lang == "python" ? "dict" : lang == "typescript" ? "object" : "object",
        _ => lang == "python" ? "Any" : "object"
    };

    private static string DetectLanguage(string code)
    {
        if (code.Contains("using System") || code.Contains("class ") && code.Contains("{") && code.Contains("void ")) return "csharp";
        if (code.Contains("def ") || code.Contains("import ") && !code.Contains(";")) return "python";
        if (code.Contains("func ") || code.Contains("package ")) return "go";
        if (code.Contains("function ") || code.Contains("const ") && code.Contains("=>")) return "javascript";
        if (code.Contains("interface ") || code.Contains(": string")) return "typescript";
        if (code.Contains("public class ") && code.Contains("extends ")) return "java";
        if (code.Contains("fn ") || code.Contains("let ") && code.Contains("->")) return "rust";
        if (code.Contains("SELECT ") || code.Contains("FROM ")) return "sql";
        if (code.Contains("<html>") || code.Contains("<!DOCTYPE")) return "html";
        return "unknown";
    }

    private static (string lang, string snippet) GetSnippet(string pattern, string lang)
    {
        return (pattern.ToLowerInvariant(), lang.ToLowerInvariant()) switch
        {
            ("rest-client", "csharp") => (lang, "using var http = new HttpClient();\nvar response = await http.GetAsync(url);\nvar body = await response.Content.ReadAsStringAsync();"),
            ("rest-client", "python") => (lang, "import httpx\nasync with httpx.AsyncClient() as client:\n    response = await client.get(url)\n    data = response.json()"),
            ("db-query", "csharp") => (lang, "using var conn = new SqlConnection(connectionString);\nawait conn.OpenAsync();\nusing var cmd = new SqlCommand(\"SELECT * FROM table WHERE id = @id\", conn);\ncmd.Parameters.AddWithValue(\"@id\", id);\nvar reader = await cmd.ExecuteReaderAsync();"),
            ("db-query", "python") => (lang, "import sqlite3\nconn = sqlite3.connect('db.sqlite')\ncursor = conn.execute('SELECT * FROM table WHERE id = ?', (id,))\nrows = cursor.fetchall()"),
            ("file-io", "csharp") => (lang, "var text = await File.ReadAllTextAsync(path);\nawait File.WriteAllTextAsync(path, content);\nvar lines = await File.ReadAllLinesAsync(path);"),
            ("file-io", "python") => (lang, "with open(path, 'r') as f:\n    text = f.read()\nwith open(path, 'w') as f:\n    f.write(content)"),
            ("sort", "python") => (lang, "sorted_items = sorted(items, key=lambda x: x['name'])\nitems.sort(key=lambda x: x['name'], reverse=True)"),
            ("sort", "csharp") => (lang, "var sorted = items.OrderBy(x => x.Name).ToList();\nvar descending = items.OrderByDescending(x => x.Name).ToList();"),
            ("filter", "python") => (lang, "filtered = [x for x in items if x['status'] == 'active']\nfiltered = list(filter(lambda x: x > 0, numbers))"),
            ("filter", "csharp") => (lang, "var filtered = items.Where(x => x.Status == \"active\").ToList();"),
            ("json-parse", "csharp") => (lang, "using var doc = JsonDocument.Parse(jsonString);\nvar root = doc.RootElement;\nvar name = root.GetProperty(\"name\").GetString();"),
            ("json-parse", "python") => (lang, "import json\ndata = json.loads(json_string)\nname = data['name']"),
            ("env-read", "csharp") => (lang, "var apiKey = Environment.GetEnvironmentVariable(\"API_KEY\")\n    ?? throw new InvalidOperationException(\"API_KEY not set\");"),
            ("env-read", "python") => (lang, "import os\napi_key = os.environ.get('API_KEY')\nif not api_key:\n    raise ValueError('API_KEY not set')"),
            _ => (lang, "// Snippet pattern not found")
        };
    }
}
