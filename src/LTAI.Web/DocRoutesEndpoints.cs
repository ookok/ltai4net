using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Tools.Search;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Web;

public static class DocRoutesEndpoints
{
    private static readonly string DocsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "output", "docs"));

    private static readonly Dictionary<string, string> Templates = new()
    {
        ["eia_report"] = "# 环境影响评价报告\n\n## 项目概况\n- 项目名称: {{PROJECT_NAME}}\n- 建设地点: {{LOCATION}}\n- 建设单位: {{COMPANY}}\n\n## 环境现状\n[ENVIRONMENT_STATUS]\n\n## 环境影响分析\n[IMPACT_ANALYSIS]\n\n## 污染防治措施\n[MITIGATION_MEASURES]\n\n## 结论\n[CONCLUSION]",
        ["emergency_plan"] = "# 突发环境事件应急预案\n\n## 总则\n[PURPOSE]\n\n## 应急组织体系\n[ORGANIZATION]\n\n## 预警与响应\n[RESPONSE]\n\n## 后期处置\n[RECOVERY]",
        ["feasibility"] = "# 可行性研究报告\n\n## 项目背景\n{{PROJECT_NAME}} - {{LOCATION}}\n\n## 建设内容\n<<SCOPE>>\n\n## 投资估算\n[INVESTMENT]\n\n## 效益分析\n[BENEFITS]",
        ["meeting_minutes"] = "# 会议纪要\n\n**会议主题**: {{TOPIC}}\n**日期**: {{DATE}}\n**参会人员**: {{ATTENDEES}}\n\n## 会议内容\n[CONTENT]\n\n## 决议事项\n[DECISIONS]\n\n## 后续工作\n[ACTION_ITEMS]",
        ["monthly_report"] = "# 月度工作报告\n\n**月份**: {{MONTH}}\n**部门**: {{DEPARTMENT}}\n\n## 本月工作完成情况\n[COMPLETED]\n\n## 存在问题\n[ISSUES]\n\n## 下月计划\n[PLANS]"
    };

    public static void MapDocRoutesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        Directory.CreateDirectory(DocsRoot);

        endpoints.MapPost("/api/doc/create", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var request = JsonSerializer.Deserialize<DocCreateRequest>(body);

                var templateType = request?.TemplateType ?? "meeting_minutes";
                var name = request?.Name ?? "untitled";
                var docId = $"{Guid.NewGuid():N}"[..12];

                var template = Templates.GetValueOrDefault(templateType, Templates["meeting_minutes"]);
                var content = template;

                if (!string.IsNullOrWhiteSpace(name))
                {
                    content = content.Replace("{{PROJECT_NAME}}", name)
                                     .Replace("{{TOPIC}}", name);
                }

                var docPath = Path.Combine(DocsRoot, $"{docId}.md");
                await File.WriteAllTextAsync(docPath, content, Encoding.UTF8);

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { id = docId, path = docPath }));
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        });

        endpoints.MapPost("/api/doc/fill", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var request = JsonSerializer.Deserialize<DocFillRequest>(body);

                if (request == null || string.IsNullOrWhiteSpace(request.DocId))
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "DocId is required" }));
                    return;
                }

                var docPath = Path.Combine(DocsRoot, $"{request.DocId}.md");
                if (!File.Exists(docPath))
                {
                    context.Response.StatusCode = 404;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Document not found" }));
                    return;
                }

                var content = await File.ReadAllTextAsync(docPath, Encoding.UTF8);

                if (request.Fields != null)
                {
                    foreach (var (key, value) in request.Fields)
                    {
                        var val = value?.ToString() ?? "";
                        content = content.Replace($"{{{{{key}}}}}", val);
                        content = content.Replace($"[{key}]", val);
                        content = content.Replace($"<<{key}>>", val);
                    }
                }

                await File.WriteAllTextAsync(docPath, content, Encoding.UTF8);

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    doc_id = request.DocId,
                    content
                }));
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        });

        endpoints.MapPost("/api/doc/annotate", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var request = JsonSerializer.Deserialize<DocAnnotateRequest>(body);

                if (request == null || string.IsNullOrWhiteSpace(request.DocId))
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "DocId is required" }));
                    return;
                }

                var docPath = Path.Combine(DocsRoot, $"{request.DocId}.md");
                if (!File.Exists(docPath))
                {
                    context.Response.StatusCode = 404;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Document not found" }));
                    return;
                }

                var content = await File.ReadAllTextAsync(docPath, Encoding.UTF8);
                var annotations = new StringBuilder();

                if (request.Citations != null && request.Citations.Count > 0)
                {
                    annotations.AppendLine("\n\n---\n## 参考文献\n");
                    for (var i = 0; i < request.Citations.Count; i++)
                    {
                        var c = request.Citations[i];
                        annotations.AppendLine($"[{i + 1}] {c.Text} — {c.Source}, 第{c.Page}页");
                    }
                    content += annotations.ToString();
                }

                await File.WriteAllTextAsync(docPath, content, Encoding.UTF8);

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    doc_id = request.DocId,
                    content,
                    citation_count = request.Citations?.Count ?? 0
                }));
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        });

        endpoints.MapPost("/api/doc/review", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var request = JsonSerializer.Deserialize<DocReviewRequest>(body);

                string content;

                if (!string.IsNullOrWhiteSpace(request?.DocId))
                {
                    var docPath = Path.Combine(DocsRoot, $"{request.DocId}.md");
                    if (!File.Exists(docPath))
                    {
                        context.Response.StatusCode = 404;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Document not found" }));
                        return;
                    }
                    content = await File.ReadAllTextAsync(docPath, Encoding.UTF8);
                }
                else if (!string.IsNullOrWhiteSpace(request?.Content))
                {
                    content = request.Content;
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "DocId or Content is required" }));
                    return;
                }

                var comments = ReviewContent(content);

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { doc_id = request?.DocId, comments }));
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        });

        endpoints.MapPost("/api/doc/review/auto", async (HttpContext context) =>
        {
            try
            {
                var file = context.Request.Form.Files.FirstOrDefault();

                if (file == null)
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "No file uploaded" }));
                    return;
                }

                if (!file.FileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Only .docx files are supported" }));
                    return;
                }

                string extractedText;
                try
                {
                    using var stream = file.OpenReadStream();
                    extractedText = ExtractTextFromDocx(stream);
                }
                catch
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Failed to parse .docx file" }));
                    return;
                }

                var issues = new List<object>();

                var emptySections = Regex.Matches(extractedText, @"#+\s*(.+?)\n\s*\n");
                foreach (Match match in emptySections)
                {
                    issues.Add(new
                    {
                        type = "empty_section",
                        severity = "major",
                        message = $"章节 '{match.Groups[1].Value.Trim()}' 似乎没有内容",
                        suggestion = "请添加该章节的具体内容"
                    });
                }

                var wordCount = extractedText.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
                if (wordCount < 100)
                {
                    issues.Add(new
                    {
                        type = "word_count",
                        severity = "blocker",
                        message = $"文档字数不足 ({wordCount} 字)，最少需要100字",
                        suggestion = "请扩充文档内容"
                    });
                }

                var headingMatches = Regex.Matches(extractedText, @"^#{1,6}\s", RegexOptions.Multiline);
                if (headingMatches.Count == 0)
                {
                    issues.Add(new
                    {
                        type = "heading_hierarchy",
                        severity = "major",
                        message = "文档没有标题层级结构",
                        suggestion = "请使用 Markdown 标题 (# ## ###) 组织文档结构"
                    });
                }

                var referenceCount = Regex.Matches(extractedText, @"\[(\d+)\]|参考文献", RegexOptions.IgnoreCase).Count;
                if (referenceCount == 0)
                {
                    issues.Add(new
                    {
                        type = "reference_count",
                        severity = "minor",
                        message = "文档没有引用参考文献",
                        suggestion = "请添加参考文献以增强文档可信度"
                    });
                }

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    file_name = file.FileName,
                    word_count = wordCount,
                    heading_count = headingMatches.Count,
                    issues
                }));
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        });

        endpoints.MapPost("/api/doc/diagram", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var request = JsonSerializer.Deserialize<DocDiagramRequest>(body);

                var diagramType = request?.Type ?? "process_flow";
                var title = request?.Title ?? diagramType;

                string diagram = diagramType.ToLower() switch
                {
                    "contour" => $"+----------+\n|  {title}  |\n| 等高线图  |\n|  (占位)   |\n+----------+",
                    "process_flow" => $"┌──────────┐     ┌──────────┐     ┌──────────┐\n│  开始    │────>│  处理    │────>│  结束    │\n└──────────┘     └──────────┘     └──────────┘",
                    "site_plan" => $"+-------------------------+\n|       {title}          |\n|   厂区平面布置图 (占位)  |\n|                         |\n+-------------------------+",
                    "noise" => $"频率 (Hz)  噪声级 (dB)\n  125        45  ■\n  250        52  ■■■\n  500        48  ■■\n 1000        42  ■\n 2000        38  ■",
                    "monitoring" => $"监测点位分布图\n  N\n  |\nW-+-E  ★ 监测点\n  |\n  S\n\n点位1: 厂界东  (113.321, 23.145)\n点位2: 厂界南  (113.320, 23.142)",
                    "causal" => $"┌──────────┐     ┌──────────┐\n│  原因A   │────>│          │\n└──────────┘     │  影响    │\n┌──────────┐     │  ({title}) │\n│  原因B   │────>│          │\n└──────────┘     └──────────┘",
                    "risk" => $"风险评估矩阵\n        可能性\n      低  中  高\n后 高 [  ] [■ ] [■■]\n果 中 [  ] [■ ] [■ ]\n   低 [  ] [  ] [  ]",
                    "base_map" => $"┌────────────────────────────┐\n│        {title}            │\n│   ┌──┐  ┌──┐  ┌──┐       │\n│   │A │  │B │  │C │       │\n│   └──┘  └──┘  └──┘       │\n│                            │\n│       ==道路==             │\n│                            │\n│   ┌──┐       ┌──┐         │\n│   │D │       │E │         │\n│   └──┘       └──┘         │\n└────────────────────────────┘",
                    _ => $"diagram placeholder for {diagramType}"
                };

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    type = diagramType,
                    title,
                    diagram
                }));
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        });

        endpoints.MapPost("/api/doc/search", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var request = JsonSerializer.Deserialize<DocSearchRequest>(body);

                if (request == null || string.IsNullOrWhiteSpace(request.Query))
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Query is required" }));
                    return;
                }

                var allResults = new List<object>();
                var maxResults = request.MaxResults ?? 10;

                var searchEngine = endpoints.ServiceProvider.GetService<UnifiedSearchEngine>();
                if (searchEngine != null)
                {
                    try
                    {
                        var webResults = await searchEngine.SearchAsync(request.Query, maxResults: maxResults);
                        allResults.AddRange(webResults.Select(r => new
                        {
                            source = "web",
                            title = r.Title,
                            url = r.Url,
                            snippet = r.Snippet,
                            relevance = r.Relevance
                        }));
                    }
                    catch { /* non-fatal */ }
                }

                if (allResults.Count < maxResults)
                {
                    try
                    {
                        var encoded = Uri.EscapeDataString(request.Query);
                        var url = $"https://html.duckduckgo.com/html/?q={encoded}";
                        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                        var html = await http.GetStringAsync(url);
                        var linkMatches = Regex.Matches(html, @"<a[^>]*class=""result__a""[^>]*href=""([^""]+)""[^>]*>([^<]+)</a>");

                        for (var i = 0; i < Math.Min(linkMatches.Count, maxResults - allResults.Count); i++)
                        {
                            allResults.Add(new { source = "web", title = linkMatches[i].Groups[2].Value.Trim(), url = linkMatches[i].Groups[1].Value, snippet = "", relevance = 1.0 - i * 0.1 });
                        }
                    }
                    catch { /* non-fatal */ }
                }

                if (Directory.Exists(DocsRoot))
                {
                    foreach (var file in Directory.GetFiles(DocsRoot, "*.md", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var docContent = await File.ReadAllTextAsync(file, Encoding.UTF8);
                            if (docContent.Contains(request.Query, StringComparison.OrdinalIgnoreCase))
                            {
                                allResults.Add(new
                                {
                                    source = "docs",
                                    title = Path.GetFileNameWithoutExtension(file),
                                    url = $"file://{file}",
                                    snippet = docContent.Length > 200 ? docContent[..200] : docContent,
                                    relevance = 0.5
                                });
                            }
                        }
                        catch { /* non-fatal */ }
                    }
                }

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    query = request.Query,
                    total = allResults.Count,
                    results = allResults.Take(maxResults)
                }));
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        });

        endpoints.MapGet("/api/doc/templates", async (HttpContext context) =>
        {
            context.Response.ContentType = "application/json";
            var templateList = Templates.Keys.Select(k => new
            {
                id = k,
                name = k switch
                {
                    "eia_report" => "环境影响评价报告",
                    "emergency_plan" => "突发环境事件应急预案",
                    "feasibility" => "可行性研究报告",
                    "meeting_minutes" => "会议纪要",
                    "monthly_report" => "月度工作报告",
                    _ => k
                },
                fields = ExtractTemplateFields(Templates[k])
            });
            await context.Response.WriteAsync(JsonSerializer.Serialize(templateList));
        });

        endpoints.MapPost("/api/doc/export", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var request = JsonSerializer.Deserialize<DocExportRequest>(body);

                if (request == null || string.IsNullOrWhiteSpace(request.DocId))
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "DocId is required" }));
                    return;
                }

                var docPath = Path.Combine(DocsRoot, $"{request.DocId}.md");
                if (!File.Exists(docPath))
                {
                    context.Response.StatusCode = 404;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Document not found" }));
                    return;
                }

                var content = await File.ReadAllTextAsync(docPath, Encoding.UTF8);
                var format = request.Format?.ToLower() ?? "md";

                string exportedContent;
                string contentType;

                switch (format)
                {
                    case "html":
                        exportedContent = ConvertMarkdownToHtml(content);
                        contentType = "text/html";
                        break;
                    case "md":
                        exportedContent = content;
                        contentType = "text/markdown";
                        break;
                    case "pdf":
                    case "docx":
                        exportedContent = content;
                        contentType = "text/markdown";
                        break;
                    default:
                        context.Response.StatusCode = 400;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = $"Unsupported format: {format}" }));
                        return;
                }

                context.Response.ContentType = contentType;
                context.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{request.DocId}.{format}\"";
                await context.Response.WriteAsync(exportedContent);
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        });
    }

    private static List<object> ReviewContent(string content)
    {
        var comments = new List<object>();

        if (string.IsNullOrWhiteSpace(content))
        {
            comments.Add(new { severity = "blocker", line = 0, message = "文档内容为空", suggestion = "请填写文档内容" });
            return comments;
        }

        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length > 500)
            {
                comments.Add(new { severity = "minor", line = i + 1, message = "行内容过长，建议分行", suggestion = "将长段落拆分为多个短段落" });
            }
        }

        if (!content.Contains('#') && !content.Contains("##"))
        {
            comments.Add(new { severity = "major", line = 0, message = "缺少标题结构", suggestion = "使用 # 和 ## 组织文档结构" });
        }

        if (Regex.IsMatch(content, @"[\[<]{{2}|[>\]}}{2}"))
        {
            comments.Add(new { severity = "blocker", line = 0, message = "文档存在未填充的模板占位符", suggestion = "请使用 /api/doc/fill 填充所有字段" });
        }

        var wordCount = content.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount < 50)
        {
            comments.Add(new { severity = "major", line = 0, message = $"文档字数较少 ({wordCount} 字)", suggestion = "建议扩充文档内容" });
        }

        if (comments.Count == 0)
        {
            comments.Add(new { severity = "suggestion", line = 0, message = "文档基本合格", suggestion = "可以进一步完善细节" });
        }

        return comments;
    }

    private static List<string> ExtractTemplateFields(string template)
    {
        var fields = new HashSet<string>();

        foreach (Match match in Regex.Matches(template, @"\{\{(\w+)\}\}"))
            fields.Add(match.Groups[1].Value);

        foreach (Match match in Regex.Matches(template, @"\[(\w+)\]"))
            fields.Add(match.Groups[1].Value);

        foreach (Match match in Regex.Matches(template, @"<<(\w+)>>"))
            fields.Add(match.Groups[1].Value);

        return fields.ToList();
    }

    private static string ExtractTextFromDocx(Stream stream)
    {
        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
        var documentEntry = archive.GetEntry("word/document.xml");
        if (documentEntry == null)
            throw new InvalidOperationException("Not a valid .docx file");

        using var entryStream = documentEntry.Open();
        using var reader = new StreamReader(entryStream, Encoding.UTF8);
        var xml = reader.ReadToEnd();

        var textSegments = Regex.Matches(xml, @"<w:t[^>]*>([^<]*)</w:t>");
        var sb = new StringBuilder();
        foreach (Match match in textSegments)
        {
            var text = match.Groups[1].Value;
            sb.Append(text);
        }

        var result = sb.ToString();

        var paragraphMatches = Regex.Matches(xml, @"<w:p[ >]");
        result = Regex.Replace(result, "(.{80})", "$1\n");

        return result;
    }

    private static string ConvertMarkdownToHtml(string markdown)
    {
        var html = markdown;

        html = Regex.Replace(html, @"^### (.+)$", "<h3>$1</h3>", RegexOptions.Multiline);
        html = Regex.Replace(html, @"^## (.+)$", "<h2>$1</h2>", RegexOptions.Multiline);
        html = Regex.Replace(html, @"^# (.+)$", "<h1>$1</h1>", RegexOptions.Multiline);

        html = Regex.Replace(html, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        html = Regex.Replace(html, @"\*(.+?)\*", "<em>$1</em>");

        html = Regex.Replace(html, @"^- (.+)$", "<li>$1</li>", RegexOptions.Multiline);
        html = Regex.Replace(html, @"(\n<li>)", "\n<ul>\n<li>");
        html = Regex.Replace(html, @"(</li>\n(?!\s*<li>))", "</li>\n</ul>\n");

        var paragraphs = html.Split("\n\n");
        var result = new StringBuilder();
        result.AppendLine("<html><body>");
        foreach (var p in paragraphs)
        {
            var trimmed = p.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.StartsWith("<h") || trimmed.StartsWith("<ul") || trimmed.StartsWith("<li"))
                result.AppendLine(trimmed);
            else
                result.AppendLine($"<p>{trimmed}</p>");
        }
        result.AppendLine("</body></html>");

        return result.ToString();
    }
}

public sealed record DocCreateRequest
{
    public string TemplateType { get; init; } = "meeting_minutes";
    public string Name { get; init; } = string.Empty;
}

public sealed record DocFillRequest
{
    public string DocId { get; init; } = string.Empty;
    public Dictionary<string, object?>? Fields { get; init; }
}

public sealed record DocAnnotateRequest
{
    public string DocId { get; init; } = string.Empty;
    public List<Citation>? Citations { get; init; }
}

public sealed record Citation(string Source, string Text, int Page);

public sealed record DocReviewRequest
{
    public string? DocId { get; init; }
    public string? Content { get; init; }
}

public sealed record DocDiagramRequest
{
    public string Type { get; init; } = "process_flow";
    public string? Title { get; init; }
    public Dictionary<string, object?>? Data { get; init; }
}

public sealed record DocSearchRequest
{
    public string Query { get; init; } = string.Empty;
    public string[]? Domains { get; init; }
    public int? MaxResults { get; init; } = 10;
}

public sealed record DocExportRequest
{
    public string DocId { get; init; } = string.Empty;
    public string Format { get; init; } = "md";
}
