using System.Text.Json;
using System.Text.RegularExpressions;

// Data Analysis 辅助脚本：统计摘要
// 接收 JSON: { "values": [1,2,3,...] } 或 { "csv": "col1,col2\n1,2\n3,4" }
var input = args.Length > 0 ? args[0] : "{}";
using var doc = JsonDocument.Parse(input);
var root = doc.RootElement;

if (root.TryGetProperty("values", out var values))
{
    var nums = values.EnumerateArray().Select(v => v.GetDouble()).ToArray();
    var result = new {
        count = nums.Length,
        sum = nums.Sum(),
        avg = nums.Average(),
        min = nums.Min(),
        max = nums.Max(),
        stddev = Math.Sqrt(nums.Sum(x => Math.Pow(x - nums.Average(), 2)) / nums.Length)
    };
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
}
else if (root.TryGetProperty("csv", out var csv))
{
    var lines = csv.GetString()?.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    if (lines != null && lines.Length > 0)
    {
        var headers = lines[0].Split(',');
        var rows = lines.Skip(1).Select(l => l.Split(',')).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { headers, rows, total = rows.Length }));
    }
}
