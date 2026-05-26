using LTAI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

public sealed class MdToolSynthesizer
{
    private readonly IChatClient _llm;
    private readonly ToolLoader _loader;
    private readonly ToolService? _toolService;
    private readonly ILogger<MdToolSynthesizer> _logger;

    public MdToolSynthesizer(IChatClient llm, ToolLoader loader,
        ToolService? toolService, ILogger<MdToolSynthesizer> logger)
    {
        _llm = llm;
        _loader = loader;
        _toolService = toolService;
        _logger = logger;
    }

    public async Task<MkTool?> SynthesizeAsync(string description, string domain = "general",
        MkToolType? preferredType = null, CancellationToken ct = default)
    {
        var prompt = BuildSynthesisPrompt(description, domain, preferredType);

        string response;
        try
        {
            var result = await _llm.GetResponseAsync(
                new List<ChatMessage>
                {
                    new(ChatRole.User, prompt)
                }, cancellationToken: ct);
            response = result.Text ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MdToolSynthesizer: LLM call failed for domain={Domain}", domain);
            return null;
        }

        var mdText = ExtractMarkdownBlock(response);
        if (mdText == null)
        {
            _logger.LogWarning("MdToolSynthesizer: No valid markdown block in LLM response");
            return null;
        }

        var parsed = _loader.Parse("synthesized.md", mdText);
        if (parsed == null)
        {
            _logger.LogWarning("MdToolSynthesizer: Parse returned null");
            return null;
        }

        if (_toolService != null)
        {
            var saved = await _toolService.CreateAndSaveAsync(
                parsed.Name, parsed.Type, parsed.Description,
                parsed.Template ?? "", parsed.Domain,
                parsed.Parameters, parsed.Triggers, parsed.Tags, ct);
            _logger.LogInformation("MdToolSynthesizer: Created tool {Name} ({Domain})",
                saved.Name, saved.Domain);
            return saved;
        }

        return parsed;
    }

    public string BuildSynthesisPrompt(string description, string domain, MkToolType? preferredType)
    {
        var typeHint = preferredType.HasValue
            ? $"Use type: {preferredType.Value.ToString().ToLower()}"
            : "Choose the most appropriate type: shell (command-line), http (API call), compose (multi-step chain), prompt (LLM template), or service (C# class method)";

        return @"You are a tool designer for the LTAI AI agent framework. Given a natural language description of what the tool should do, generate a complete `.md` tool definition following this EXACT format:

```
# tool: <tool_name>
domain: __DOMAIN__
type: <shell|http|compose|prompt|service>
description: <brief description>

## parameters
- param_name: type (required) (default: value) — description
... (all required parameters)

## command (for type: shell)
<template with {{param}} {{placeholder}} syntax>

## triggers
- pattern: ""Chinese trigger"" (weight: 1.0)
- pattern: ""English trigger"" (weight: 0.9)

## tags
- domain_tag
- function_tag
```

Rules:
- Tool name: lowercase alphanumeric with underscores
- For shell type: use {{param}} for variable substitution
- For service type: use ## service with name: <full_class_name> and method: <method_name>
- For compose type: use ## steps with - step_name and command: tool_name
- Parameters should reflect what the description requires
- Triggers must include at least one Chinese and one English pattern
- Tags should include the domain and functional category
- Timeout defaults: 30 for shell, 60 for http, 120 for compose
- max_output_lines defaults to 100

__TYPEHINT__

Task description: __DESCRIPTION__

Return ONLY the markdown block (between ```md and ```), nothing else.
"
            .Replace("__DOMAIN__", domain)
            .Replace("__TYPEHINT__", typeHint)
            .Replace("__DESCRIPTION__", description);
    }

    private string? ExtractMarkdownBlock(string response)
    {
        var start = response.IndexOf("```md", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            start = response.IndexOf("```", StringComparison.Ordinal);
            if (start < 0) return null;
        }

        start = response.IndexOf('\n', start) + 1;
        if (start <= 0) return null;

        var end = response.IndexOf("\n```", start, StringComparison.Ordinal);
        if (end < 0)
        {
            end = response.IndexOf("```", start, StringComparison.Ordinal);
            if (end < 0) return null;
        }

        return response[start..end].Trim();
    }
}
