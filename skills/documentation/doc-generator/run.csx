#r "nuget: System.Text.Json, 10.0.0"
using System.Text.Json;

// Doc Generator 辅助脚本：格式化文档输出
// 接收 JSON 参数: { "title": "...", "content": "...", "format": "markdown|html" }
var input = args.Length > 0 ? args[0] : "{}";
var json = JsonDocument.Parse(input);
var title = json.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
var content = json.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
var format = json.RootElement.TryGetProperty("format", out var f) ? f.GetString() ?? "markdown" : "markdown";

var result = format switch
{
    "html" => $"<h1>{System.Net.WebUtility.HtmlEncode(title)}</h1>\n{content}",
    _ => $"# {title}\n\n{content}"
};

Console.WriteLine(result);
