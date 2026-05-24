using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.AI.Governors;

public sealed record GroundingResult
{
    public bool IsGrounded { get; init; }
    public string? Issue { get; init; }
    public string? RetryInstruction { get; init; }
    public float Confidence { get; init; }
    public string CheckType { get; init; } = "heuristic";

    public static GroundingResult Grounded => new() { IsGrounded = true, Confidence = 1.0f };
}

public sealed class ResponseGroundingVerifier
{
    private const int MinHonestResponseLength = 10;
    private static readonly Regex ToolUsageClaim = new(
        @"(?:已使用|执行了|调用了|运行了|通过|使用了)\s*(?:shell_exec|web_search|filesystem_read|git_diff|git_log|filesystem_list|http_get|env_\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SpeculationPattern = new(
        @"(?:可能|或许|大概|也许|建议|可以尝试|推测|猜测|估计)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HonestyDenial = new(
        @"(?:未找到|没有找到|不存在|无法找到|没有相关信息|无结果|空)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DeflectionPattern = new(
        @"(?:I am unable|I cannot|I can't|抱歉.*无法|无法提供|作为.*AI|simulated|teacher model|没有.*能力|没有.*权限)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public GroundingResult Verify(
        string response,
        string query,
        string? toolContext,
        bool toolsWereActuallyCalled,
        int toolCallCount,
        bool layer1ContextWasInjected)
    {
        // Check 1: Tool usage claim without actual tool call
        if (!toolsWereActuallyCalled && !layer1ContextWasInjected)
        {
            var toolClaimMatch = ToolUsageClaim.Match(response);
            if (toolClaimMatch.Success)
            {
                return new GroundingResult
                {
                    IsGrounded = false,
                    Issue = $"回答声称使用了工具（\"{toolClaimMatch.Value}\"），但实际未产生任何 tool_call。这是编造行为。",
                    RetryInstruction = "你的上一轮回答声称使用了工具但实际并未调用。请重新回答：要么调用工具获取真实数据，要么如实告知无法回答。不得编造工具调用描述。",
                    Confidence = 0.1f,
                    CheckType = "false_tool_claim"
                };
            }
        }

        // Check 2: Tool result had data but answer says nothing/empty
        if (toolContext != null && !string.IsNullOrWhiteSpace(toolContext)
            && !toolContext.Contains("工具返回了空结果")
            && !toolContext.Contains("未找到任何相关结果")
            && !toolContext.Contains("\"error\""))
        {
            var isHonestyDenial = HonestyDenial.IsMatch(response);
            var isTooShort = response.Trim().Length < MinHonestResponseLength;

            if (isHonestyDenial && toolContext.Length > 100)
            {
                return new GroundingResult
                {
                    IsGrounded = false,
                    Issue = "工具返回了实际数据，但回答声称未找到内容。回答与工具结果矛盾。",
                    RetryInstruction = "上一轮回答声称未找到信息，但工具实际返回了数据。请严格基于已提供的工具结果重新回答，不要编造或忽略已有数据。",
                    Confidence = 0.2f,
                    CheckType = "context_denial"
                };
            }

            if (isTooShort && toolContext.Length > 200)
            {
                return new GroundingResult
                {
                    IsGrounded = false,
                    Issue = "工具返回了大量数据，但回答过于简短，可能忽略了关键信息。",
                    RetryInstruction = "上一轮回答过于简短。请严格基于已提供的工具结果，给出更详细、更有帮助的回答。",
                    Confidence = 0.4f,
                    CheckType = "too_short"
                };
            }
        }

        // Check 3: Tool result was empty/error, check for honesty
        if (toolContext != null && (toolContext.Contains("工具返回了空结果")
            || toolContext.Contains("未找到任何相关结果")
            || toolContext.Contains("\"error\"")))
        {
            var hasSpeculation = SpeculationPattern.IsMatch(response);
            if (hasSpeculation)
            {
                return new GroundingResult
                {
                    IsGrounded = false,
                    Issue = "工具未返回有效数据，但回答中包含推测性内容（如\"可能\"、\"建议\"等）。工具无数据时不得推测。",
                    RetryInstruction = "上一轮回答在工具无返回数据的情况下进行了推测。请严格如实告知用户：工具未找到相关信息。不得添加任何推测或建议。",
                    Confidence = 0.3f,
                    CheckType = "speculation_on_empty"
                };
            }
        }

        // Check 4: Data grounding with specific items (only when we have structured tool context)
        if (toolContext != null && layer1ContextWasInjected
            && !toolContext.Contains("未找到")
            && !toolContext.Contains("\"error\""))
        {
            var toolItems = ExtractToolItems(toolContext);
            if (toolItems.Count > 0)
            {
                var responseItems = ExtractResponseItems(response);
                if (responseItems.Count > 0)
                {
                    var fabricatedItems = new List<string>();
                    foreach (var ri in responseItems)
                    {
                        var found = toolItems.Any(ti =>
                            ti.Contains(ri, StringComparison.OrdinalIgnoreCase)
                            || ri.Contains(ti, StringComparison.OrdinalIgnoreCase));
                        if (!found)
                            fabricatedItems.Add(ri);
                    }

                    if (fabricatedItems.Count >= responseItems.Count * 0.5f && fabricatedItems.Count >= 3)
                    {
                        return new GroundingResult
                        {
                            IsGrounded = false,
                            Issue = $"回答中至少 {fabricatedItems.Count}/{responseItems.Count} 个条目不在工具结果中，可能编造。编造示例: {string.Join(", ", fabricatedItems.Take(3))}",
                            RetryInstruction = "上一轮回答包含了工具返回数据中不存在的条目。请严格基于系统消息中的【Layer1 自动执行工具】或【自动网络搜索结果】数据回答，不要添加任何不在其中的名称、数字或事实。",
                            Confidence = 0.15f,
                            CheckType = "fabricated_items"
                        };
                    }
                }
            }
        }

        // Check 5: Honest deflection — model admits inability without trying tools.
        // When tool context exists, the system has data the model should use.
        if (toolContext != null && !string.IsNullOrWhiteSpace(toolContext)
            && !toolContext.Contains("工具返回了空结果")
            && !toolContext.Contains("未找到任何相关结果"))
        {
            if (DeflectionPattern.IsMatch(response))
            {
                return new GroundingResult
                {
                    IsGrounded = false,
                    Issue = "回答声称无法处理（I am unable / 无法提供），但系统已通过 Layer1 提供了工具数据。模型应基于已有数据回答而非放弃。",
                    RetryInstruction = "上一轮你声称无法回答，但实际上方已提供了工具获取的真实数据。请严格基于系统消息中的工具结果重新回答，不要放弃。",
                    Confidence = 0.25f,
                    CheckType = "deflection"
                };
            }
        }

        return GroundingResult.Grounded;
    }

    private static List<string> ExtractToolItems(string toolContext)
    {
        var items = new List<string>();

        try
        {
            if (toolContext.Contains("\"items\"") || toolContext.Contains("\"results\""))
            {
                using var doc = JsonDocument.Parse(toolContext);
                var root = doc.RootElement;

                if (root.TryGetProperty("items", out var itemsArr))
                {
                    foreach (var item in itemsArr.EnumerateArray())
                    {
                        if (item.TryGetProperty("name", out var name))
                            items.Add(name.GetString() ?? "");
                        if (item.TryGetProperty("title", out var title))
                            items.Add(title.GetString() ?? "");
                        if (item.TryGetProperty("snippet", out var snippet))
                            items.Add(snippet.GetString() ?? "");
                    }
                }

                if (root.TryGetProperty("results", out var resultsArr))
                {
                    foreach (var r in resultsArr.EnumerateArray())
                    {
                        if (r.TryGetProperty("title", out var t))
                            items.Add(t.GetString() ?? "");
                    }
                }
            }
        }
        catch { }

        return items;
    }

    private static List<string> ExtractResponseItems(string response)
    {
        var items = new List<string>();

        var bulletMatch = Regex.Matches(response, @"(?:^|\n)\s*[-*•]\s*(.+?)(?:\n|$)", RegexOptions.Multiline);
        foreach (Match m in bulletMatch)
        {
            var item = m.Groups[1].Value.Trim();
            if (item.Length > 1 && item.Length < 100)
                items.Add(item);
        }

        var numberedMatch = Regex.Matches(response, @"(?:^|\n)\s*\d+[.)]\s*(.+?)(?:\n|$)", RegexOptions.Multiline);
        foreach (Match m in numberedMatch)
        {
            var item = m.Groups[1].Value.Trim();
            if (item.Length > 1 && item.Length < 100 && !items.Contains(item))
                items.Add(item);
        }

        return items;
    }
}
