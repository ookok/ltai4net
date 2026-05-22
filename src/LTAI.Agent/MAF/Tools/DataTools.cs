using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.Agent.Tools;

[Description("Data parsing and transformation tools for JSON, CSV, XML")]
public sealed class DataTools
{
    [Description("Parse a CSV string and return as JSON array of objects. First row is used as headers.")]
    public static string ParseCsv(
        [Description("CSV content as string")] string csv,
        [Description("Delimiter character, default comma")] string delimiter = ",")
    {
        try
        {
            var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2)
                return JsonSerializer.Serialize(new { error = "CSV must have at least header and one data row" });

            var headers = lines[0].Split(delimiter).Select(h => h.Trim().Trim('"')).ToArray();
            var rows = new List<Dictionary<string, string>>();
            for (int i = 1; i < Math.Min(lines.Length, 1001); i++)
            {
                var values = lines[i].Split(delimiter);
                var row = new Dictionary<string, string>();
                for (int j = 0; j < Math.Min(headers.Length, values.Length); j++)
                    row[headers[j]] = values[j].Trim().Trim('"');
                rows.Add(row);
            }
            return JsonSerializer.Serialize(new { delimiter, totalRows = lines.Length - 1, rows = rows.Take(100) });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Query a JSON object or array using a JSONPath expression. Returns matched elements.")]
    public static string QueryJson(
        [Description("JSON string to query")] string json,
        [Description("JSONPath expression, e.g. '$.store.book[*].author', '$[?(@.price<10)]'")] string jsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var results = new List<string>();
            WalkJsonPath(doc.RootElement, jsonPath, results);
            return JsonSerializer.Serialize(new { jsonPath, matchCount = results.Count, results = results.Take(200) });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private static void WalkJsonPath(JsonElement element, string path, List<string> results)
    {
        if (path == "$" || path == "$.*")
        {
            results.Add(element.GetRawText());
            return;
        }

        var segments = path.TrimStart('$').TrimStart('.').Split('.');
        if (path.Contains("[*]"))
        {
            var basePath = path[..path.IndexOf("[*]")].TrimStart('$').TrimStart('.');
            WalkAllArrayElements(element, basePath, results);
            return;
        }

        WalkByPath(element, segments, results);
    }

    private static void WalkByPath(JsonElement element, string[] segments, List<string> results, int depth = 0)
    {
        if (depth >= segments.Length) { results.Add(element.GetRawText()); return; }
        var seg = segments[depth].TrimEnd(']').Split('[');
        if (element.TryGetProperty(seg[0], out var child))
        {
            if (seg.Length > 1 && int.TryParse(seg[1], out var idx) && child.ValueKind == JsonValueKind.Array)
            {
                var arr = child.EnumerateArray().ToList();
                if (idx < arr.Count) WalkByPath(arr[idx], segments, results, depth + 1);
            }
            else
            {
                WalkByPath(child, segments, results, depth + 1);
            }
        }
    }

    private static void WalkAllArrayElements(JsonElement element, string basePath, List<string> results)
    {
        var segments = basePath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var current = element;
        foreach (var seg in segments)
        {
            if (current.TryGetProperty(seg, out var child))
                current = child;
            else
            {
                results.Add($"Property '{seg}' not found");
                return;
            }
        }
        if (current.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in current.EnumerateArray().Take(200))
                results.Add(item.GetRawText());
        }
        else
        {
            results.Add(current.GetRawText());
        }
    }

    [Description("Convert between JSON and CSV format.")]
    public static string ConvertFormat(
        [Description("Input data string")] string data,
        [Description("Source format: json or csv")] string sourceFormat,
        [Description("Target format: json or csv")] string targetFormat)
    {
        try
        {
            if (sourceFormat.Equals("json", StringComparison.OrdinalIgnoreCase) && targetFormat.Equals("csv", StringComparison.OrdinalIgnoreCase))
            {
                return JsonToCsv(data);
            }
            if (sourceFormat.Equals("csv", StringComparison.OrdinalIgnoreCase) && targetFormat.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                return ParseCsv(data);
            }
            return JsonSerializer.Serialize(new { error = "Unsupported conversion" });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private static string JsonToCsv(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList()
            : new List<JsonElement> { doc.RootElement };

        if (items.Count == 0) return "";

        var headers = new HashSet<string>();
        foreach (var item in items)
            foreach (var prop in item.EnumerateObject())
                headers.Add(prop.Name);
        var headerList = headers.ToList();

        var lines = new List<string> { string.Join(",", headerList.Select(EscapeCsv)) };
        foreach (var item in items.Take(500))
        {
            var values = headerList.Select(h =>
                item.TryGetProperty(h, out var v) ? EscapeCsv(v.ToString()) : "");
            lines.Add(string.Join(",", values));
        }
        return string.Join("\n", lines);
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    [Description("Pretty-print any structured data as an indented JSON tree.")]
    public static string PrettyPrint(
        [Description("JSON string to pretty-print")] string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var formatted = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            return formatted;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [Description("Extract a specific property from each object in a JSON array. Returns an array of those values.")]
    public static string Pluck(
        [Description("JSON array string")] string jsonArray,
        [Description("The property name to extract from each object")] string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonArray);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return JsonSerializer.Serialize(new { error = "Input must be a JSON array" });

            var values = doc.RootElement.EnumerateArray()
                .Select(item => item.TryGetProperty(propertyName, out var val) ? val.GetRawText() : null)
                .Where(v => v != null)
                .ToList();
            return JsonSerializer.Serialize(new { property = propertyName, count = values.Count, values });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}
