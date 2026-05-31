using System.ComponentModel;
using System.Text.Json;
using LTAI.AI;
using LTAI.Core;

namespace LTAI.Agent.Tools;

[ToolDomain("data")]
public sealed class DataTransformTools
{
    private readonly string _ws;
    public DataTransformTools(string ws) => _ws = ws;

    [Description("对 JSON 数据执行路径查询点分路径如 'store.book[0].title'。\n"
        + "适用场景：从 JSON 中提取特定字段、验证 JSON 结构、数据探索。\n"
        + "关键参数：json — JSON 字符串；path — 点分路径表达式。")]
    public string JsonQuery(string json, string path)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var result = EvaluateJsonPath(doc.RootElement, path);
            return result ?? "Path not found";
        }
        catch (Exception ex)
        {
            return $"JSON query error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [Description("读取 CSV 文件并返回 JSON 格式。第一行为列标题。\n"
        + "适用场景：查看 CSV 数据、导入电子表格数据、数据预处理。\n"
        + "关键参数：path — CSV 文件路径。")]
    public string CsvRead(string path)
    {
        var fp = PathUtils.SafeResolvePath(_ws, path);
        if (fp == null) return "Error: path escape";
        if (!File.Exists(fp)) return $"File not found: {fp}";

        try
        {
            var lines = File.ReadAllLines(fp);
            if (lines.Length == 0) return "Empty file";

            var headers = ParseCsvLine(lines[0]);
            var rows = new List<Dictionary<string, string>>();

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var values = ParseCsvLine(lines[i]);
                var row = new Dictionary<string, string>();
                for (int j = 0; j < headers.Length && j < values.Length; j++)
                    row[headers[j]] = values[j];
                rows.Add(row);
            }

            var result = System.Text.Json.JsonSerializer.Serialize(new { headers, rows, totalRows = rows.Count },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            return result;
        }
        catch (Exception ex)
        {
            return $"CSV read error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [Description("将 JSON 数据写入 CSV 文件。输入应为对象数组 JSON。\n"
        + "适用场景：数据导出、将数据库查询结果保存为 CSV。\n"
        + "关键参数：path — 输出 CSV 路径；jsonData — 对象数组 JSON。")]
    public string CsvWrite(string path, string jsonData)
    {
        var fp = PathUtils.SafeResolvePath(_ws, path);
        if (fp == null) return "Error: path escape";

        try
        {
            var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(jsonData);
            if (rows == null || rows.Count == 0)
                return "Error: empty or invalid JSON data";

            var headers = rows[0].Keys.ToArray();
            var lines = new List<string> { string.Join(",", headers.Select(EscapeCsvField)) };

            foreach (var row in rows)
            {
                var values = headers.Select(h => row.TryGetValue(h, out var e) ? EscapeCsvField(e.ToString() ?? "") : "");
                lines.Add(string.Join(",", values));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
            File.WriteAllLines(fp, lines);
            return $"CSV written: {fp} ({rows.Count} rows, {headers.Length} columns)";
        }
        catch (Exception ex)
        {
            return $"CSV write error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string? EvaluateJsonPath(JsonElement element, string path)
    {
        var parts = path.Split('.');
        JsonElement current = element;

        foreach (var part in parts)
        {
            var (name, index) = ParsePathPart(part);
            if (index.HasValue)
            {
                if (current.ValueKind != JsonValueKind.Array) return null;
                var arr = current.EnumerateArray().ToList();
                if (index.Value < 0 || index.Value >= arr.Count) return null;
                current = arr[index.Value];
            }

            if (name != null)
            {
                if (current.ValueKind != JsonValueKind.Object) return null;
                if (!current.TryGetProperty(name, out current)) return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => current.GetRawText()
        };
    }

    private static (string? name, int? index) ParsePathPart(string part)
    {
        var bracketStart = part.IndexOf('[');
        if (bracketStart >= 0)
        {
            var name = part[..bracketStart];
            var indexStr = part[(bracketStart + 1)..part.IndexOf(']')];
            if (int.TryParse(indexStr, out var idx))
                return (string.IsNullOrEmpty(name) ? null : name, idx);
        }
        return (part, null);
    }

    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                inQuotes = !inQuotes;
            else if (line[i] == ',' && !inQuotes)
            {
                result.Add(current.ToString().Trim('"'));
                current.Clear();
            }
            else
                current.Append(line[i]);
        }
        result.Add(current.ToString().Trim('"'));
        return [.. result];
    }

    private static string EscapeCsvField(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
