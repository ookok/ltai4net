using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record GroundingResult
{
    public bool IsGrounded { get; init; }
    public string? Issue { get; init; }
    public string? RetryInstruction { get; init; }
    public float Confidence { get; init; }
    public string CheckType { get; init; } = "structural";

    public static GroundingResult Grounded => new() { IsGrounded = true, Confidence = 1.0f };
}

public sealed class ResponseGroundingVerifier
{
    private readonly ILogger<ResponseGroundingVerifier>? _logger;
    private readonly PromptTemplateStore? _prompts;
    private const int MinResponseChars = 15;
    private const int MinContextChars = 100;
    private const float MinOverlapRatio = 0.3f;
    private const int DeflectionRatioThreshold = 10;

    public ResponseGroundingVerifier(ILogger<ResponseGroundingVerifier>? logger = null, PromptTemplateStore? prompts = null)
    {
        _logger = logger;
        _prompts = prompts;
    }

    public GroundingResult Verify(
        string response,
        string query,
        string? toolContext,
        bool toolsWereActuallyCalled,
        int toolCallCount,
        bool layer1ContextWasInjected)
    {
        if (string.IsNullOrWhiteSpace(response))
            return GroundingResult.Grounded;

        var ctxSize = toolContext?.Length ?? 0;
        var hasToolData = toolContext != null && ctxSize > MinContextChars
            && !LooksLikeEmptyResult(toolContext);

        // Check 1: Tool name mention without actual call
        if (!toolsWereActuallyCalled && !layer1ContextWasInjected)
        {
            var mentionedTool = FindMentionedTool(response);
            if (mentionedTool != null)
            {
                return new GroundingResult
                {
                    IsGrounded = false,
                    Issue = $"Response mentions tool '{mentionedTool}' but no tool call was issued. Fabrication detected.",
                    RetryInstruction = $"You claimed to use '{mentionedTool}' but did not actually call it. Regenerate using real tool data or state you cannot answer.",
                    Confidence = 0.1f,
                    CheckType = "false_tool_claim"
                };
            }
        }

        // Check 2: Tool has data but response is disproportionately small
        if (hasToolData)
        {
            if (response.Length < MinResponseChars)
            {
                return new GroundingResult
                {
                    IsGrounded = false,
                    Issue = $"Tool returned {ctxSize} chars of data but response is only {response.Length} chars — likely ignoring tool results.",
                    RetryInstruction = $"Tool returned substantial data ({ctxSize} chars). Your response was too brief. Provide a proper summary based on the tool results.",
                    Confidence = 0.35f,
                    CheckType = "response_too_short"
                };
            }

            // Response is disproportionately small compared to context
            if (ctxSize > response.Length * DeflectionRatioThreshold)
            {
                return new GroundingResult
                {
                    IsGrounded = false,
                    Issue = $"Tool context is {ctxSize} chars but response is only {response.Length} chars (ratio {ctxSize / Math.Max(1, response.Length)}:1). Model appears to be deflecting.",
                    RetryInstruction = $"The system provided {ctxSize} chars of tool data. Your response is too short relative to the available data. Summarize the tool results properly.",
                    Confidence = 0.25f,
                    CheckType = "deflection_ratio"
                };
            }
        }

        // Check 3: Tool returned no data, but response contains substantive claims
        if (toolContext != null && ctxSize > 0 && LooksLikeEmptyResult(toolContext))
        {
            var claimCount = CountSubstantiveClaims(response);
            if (claimCount >= 3)
            {
                return new GroundingResult
                {
                    IsGrounded = false,
                    Issue = $"Tool returned empty/error but response contains {claimCount} substantive claims. Model is speculating on empty data.",
                    RetryInstruction = "Tool returned empty or error results. Your previous response contained claims not supported by any data. Regenerate: state honestly that no data was found.",
                    Confidence = 0.3f,
                    CheckType = "speculation_on_empty"
                };
            }
        }

        // Check 4: Structural overlap between response and tool context
        if (hasToolData && layer1ContextWasInjected)
        {
            var toolItems = ExtractKeyItems(toolContext!);
            var responseItems = ExtractKeyItems(response);

            if (toolItems.Count >= 5 && responseItems.Count >= 3)
            {
                var overlap = toolItems.Intersect(responseItems, StringComparer.OrdinalIgnoreCase).Count();
                var overlapRatio = (float)overlap / responseItems.Count;

                if (overlapRatio < MinOverlapRatio)
                {
                    return new GroundingResult
                    {
                        IsGrounded = false,
                        Issue = $"Only {overlap}/{responseItems.Count} response items ({overlapRatio:P0} overlap) found in tool results. Likely fabrication.",
                        RetryInstruction = $"Most items in your response ({responseItems.Count - overlap}) were not found in the tool results. Regenerate strictly from the provided tool data.",
                        Confidence = 0.15f,
                        CheckType = "low_overlap"
                    };
                }
            }
        }

        // Check 5: Response has duplicate structure not from tool — hallucinated formatting
        if (hasToolData && !layer1ContextWasInjected)
        {
            var responseItems = ExtractKeyItems(response);
            var toolItems = ExtractKeyItems(toolContext!);

            if (responseItems.Count >= 4 && toolItems.Count == 0)
            {
                // Response has structured items but tool didn't → possible hallucination
                // Only flag if the items look like data (contain digits, specific names)
                var dataItems = responseItems.Count(i => i.Any(char.IsDigit) || i.Length > 20);
                if (dataItems >= 2)
                {
                    return new GroundingResult
                    {
                        IsGrounded = false,
                        Issue = $"Response contains {responseItems.Count} structured items but tool context has none. Possible hallucination.",
                        RetryInstruction = "Your response contains structured data items not found in tool results. Regenerate based only on the provided tool data.",
                        Confidence = 0.2f,
                        CheckType = "hallucinated_items"
                    };
                }
            }
        }

        return GroundingResult.Grounded;
    }

    public async Task<GroundingResult> VerifyWithLLMAsync(
        string response,
        string toolContext,
        IChatClient llm,
        string flashModel,
        CancellationToken ct = default)
    {
        var ctxSnippet = toolContext.Length > 3000 ? toolContext[..3000] : toolContext;
        var respSnippet = response.Length > 2000 ? response[..2000] : response;

        var prompt = _prompts?.Render("verify_user", new Dictionary<string, string>
        {
            ["context"] = ctxSnippet,
            ["response"] = respSnippet
        }) ?? $"Tool results:\n---\n{ctxSnippet}\n---\n\nModel answer:\n---\n{respSnippet}\n---\n\nIs every factual claim in the model answer directly supported by the tool results?\nAnswer ONLY: YES or NO, followed by a single short reason.";

        var systemPrompt = _prompts?.Render("verify_system") ?? "You verify factuality. Output ONLY 'YES' or 'NO' followed by a one-line reason.";

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, prompt)
            };
            var options = new ChatOptions
            {
                ModelId = flashModel,
                Temperature = 0f,
                MaxOutputTokens = 128,
                Tools = new List<AITool>()
            };

            var result = await llm.GetResponseAsync(messages, options, ct);
            var verdict = result.Text?.Trim() ?? "";

            if (verdict.StartsWith("NO", StringComparison.OrdinalIgnoreCase))
            {
                var reason = verdict.Length > 3 ? verdict[3..].Trim().TrimStart('.', ',', ':').Trim() : "LLM verification";
                return new GroundingResult
                {
                    IsGrounded = false,
                    Issue = $"LLM verification: {reason}",
                    RetryInstruction = $"The system verifier flagged this response as ungrounded: {reason}. Regenerate strictly from the tool data.",
                    Confidence = 0.3f,
                    CheckType = "llm_verifier"
                };
            }

            return GroundingResult.Grounded;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "LLM grounding verification skipped");
            return GroundingResult.Grounded;
        }
    }

    private static bool LooksLikeEmptyResult(string toolContext)
    {
        try
        {
            if (toolContext.Contains("\"error\"") || toolContext.Contains("\"exitCode\":1"))
                return true;

            var jsonStart = toolContext.IndexOf('{');
            if (jsonStart >= 0)
            {
                using var doc = JsonDocument.Parse(toolContext[jsonStart..]);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out _)) return true;
                if (root.TryGetProperty("exitCode", out var ec) && ec.GetInt32() != 0) return true;
                if (root.TryGetProperty("count", out var c) && c.GetInt32() == 0)
                {
                    if (root.TryGetProperty("items", out var items) && items.GetArrayLength() == 0)
                        return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static string? FindMentionedTool(string response)
    {
        var toolNames = new[] { "shell_exec", "web_search", "filesystem_read", "filesystem_list",
            "filesystem_write", "git_diff", "git_log", "git_blame", "http_get", "http_post",
            "env_sysinfo", "env_processes", "env_network", "datetime_now", "math_eval" };

        foreach (var name in toolNames)
        {
            if (response.Contains(name, StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return null;
    }

    private static int CountSubstantiveClaims(string response)
    {
        // Claims are: numbers, proper nouns (2+ uppercase/hanzi sequences), or bullet points
        var count = 0;

        var digitMatches = Regex.Matches(response, @"\b\d{2,}\b");
        count += digitMatches.Count;

        var properNounMatches = Regex.Matches(response, @"\b[A-Z\u4e00-\u9fff]{2,}\b");
        count += Math.Min(properNounMatches.Count / 3, 5);

        var bulletMatches = Regex.Matches(response, @"(?:^|\n)\s*[-•*]\s+\S");
        count += bulletMatches.Count;

        return count;
    }

    private static List<string> ExtractKeyItems(string text)
    {
        var items = new List<string>();

        // Extract JSON items from tool context
        try
        {
            var jsonStart = text.IndexOf('{');
            if (jsonStart >= 0)
            {
                using var doc = JsonDocument.Parse(text[jsonStart..]);
                ExtractFromElement(doc.RootElement, items);
            }
        }
        catch { }

        // Extract bullet/numbered list items from response
        foreach (Match m in Regex.Matches(text, @"(?:^|\n)\s*[-•*\d]+[.)]\s*(.+?)(?:\n|$)", RegexOptions.Multiline))
        {
            var item = m.Groups[1].Value.Trim();
            if (item.Length > 2 && item.Length < 120)
                items.Add(NormalizeItem(item));
        }

        // Extract table rows (pipe-delimited)
        foreach (Match m in Regex.Matches(text, @"\|([^|\n]+)\|", RegexOptions.Multiline))
        {
            var cell = m.Groups[1].Value.Trim();
            if (cell.Length > 2 && cell.Length < 120 && !cell.StartsWith('-'))
                items.Add(NormalizeItem(cell));
        }

        return items.Distinct().ToList();
    }

    private static void ExtractFromElement(JsonElement element, List<string> items, string prefix = "")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Name is "name" or "title" or "snippet" or "path" or "message" or "summary")
                    {
                        var val = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(val) && val.Length > 1)
                            items.Add(NormalizeItem(val));
                    }
                    if (prop.Name is "items" or "results" or "commits" or "processes")
                        ExtractFromElement(prop.Value, items, prefix);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    ExtractFromElement(item, items, prefix);
                break;
        }
    }

    private static string NormalizeItem(string item)
    {
        var normalized = item.Trim()
            .Replace("**", "")
            .Replace("`", "")
            .Replace("：", ":")
            .Replace("，", ",");
        return normalized.Length <= 80 ? normalized : normalized[..80];
    }
}
