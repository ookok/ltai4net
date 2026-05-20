using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Core.System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Capability.Pipeline;

public enum SinkType { Memory, Kb, Log, Stdout, Disk }
public enum SourceType { Raw, Kb, Memory, File, Text }
public enum PipelineOp { Extract, Map, Filter, Resolve, Reduce, Glean, Sort }

public record PipelineStep(PipelineOp Op, string? Prompt, string? Condition, string? Key,
    Dictionary<string, object>? Params, bool AsMarkdown, bool AsTable, bool AsList);

public record PipelineConfig(string Name, string Description, List<PipelineStep> Steps);

public record FormatRule(string Pattern, string Replacement, bool Enabled = true, MatchEvaluator? Evaluator = null);

public sealed class RuleChain
{
    private readonly List<(FormatRule Rule, Regex Regex)> _rules = new();

    public RuleChain()
    {
        AddRule(new FormatRule(@" +\n", "\n"));
        AddRule(new FormatRule(@"\n{3,}", "\n\n"));
        AddRule(new FormatRule(@"[\u2018\u2019\u201c\u201d]", "'"));
        AddRule(new FormatRule(@"<[^>]+>", ""));
        AddRule(new FormatRule(@"[\uff10-\uff19]", "", Evaluator: m => ((char)(m.Value[0] - 0xFEE0)).ToString()));
        AddRule(new FormatRule(@"[\t ]+$", ""));
    }

    public void AddRule(FormatRule rule)
    {
        _rules.Add((rule, new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.Multiline)));
    }

    public string Apply(string text)
    {
        foreach (var (rule, regex) in _rules)
        {
            if (!rule.Enabled) continue;
            text = rule.Evaluator != null ? regex.Replace(text, rule.Evaluator) : regex.Replace(text, rule.Replacement);
        }
        return text;
    }

    public void Toggle(string pattern, bool enabled)
    {
        var idx = _rules.FindIndex(r => r.Rule.Pattern == pattern);
        if (idx >= 0) _rules[idx] = (_rules[idx].Rule with { Enabled = enabled }, _rules[idx].Regex);
    }
}

public sealed class PipelineEngine
{
    private readonly ILogger<PipelineEngine> _logger;
    private static readonly RuleChain TextFormatter = new();

    public PipelineEngine(ILogger<PipelineEngine>? logger = null)
    {
        _logger = logger ?? NullLogger<PipelineEngine>.Instance;
    }

    public async Task<List<Dictionary<string, object>>> Execute(PipelineConfig config,
        List<Dictionary<string, object>> input, Func<string, string, Task<string>>? chatFn = null)
    {
        var data = input.Select(d => new Dictionary<string, object>(d)).ToList();

        foreach (var step in config.Steps)
        {
            data = step.Op switch
            {
                PipelineOp.Extract => await Extract(data, step, chatFn),
                PipelineOp.Filter => Filter(data, step),
                PipelineOp.Map => await Map(data, step, chatFn),
                PipelineOp.Sort => Sort(data, step),
                PipelineOp.Reduce => await Reduce(data, step, chatFn),
                PipelineOp.Glean => await Glean(data, step, chatFn),
                PipelineOp.Resolve => await Resolve(data, step),
                _ => data
            };
        }

        return data;
    }

    public async Task<PipelineConfig> GeneratePipeline(string description,
        Func<string, string, Task<string>> chatFn)
    {
        var prompt = $@"Design a data processing pipeline for this task: {description}

Return JSON with: name, description, steps (array of {{op: extract|map|filter|resolve|reduce|glean|sort, prompt?, condition?, key?, params?}}).

Pipeline operators:
- extract: run extraction on text fields
- map: transform data with LLM
- filter: keep matching rows
- resolve: deduplicate
- sort: sort by key
- reduce: summarize
- glean: refine/enrich";
        try
        {
            var response = await chatFn("pipeline_gen", prompt);
            var json = JsonSerializer.Deserialize<JsonElement>(ExtractJson(response));

            var steps = new List<PipelineStep>();
            if (json.TryGetProperty("steps", out var stepsJson))
            {
                foreach (var s in stepsJson.EnumerateArray())
                {
                    steps.Add(new PipelineStep(
                        Enum.TryParse<PipelineOp>(s.GetProperty("op").GetString(), true, out var op) ? op : PipelineOp.Map,
                        s.TryGetProperty("prompt", out var p) ? p.GetString() : null,
                        s.TryGetProperty("condition", out var c) ? c.GetString() : null,
                        s.TryGetProperty("key", out var k) ? k.GetString() : "text",
                        s.TryGetProperty("params", out var pa) ? JsonSerializer.Deserialize<Dictionary<string, object>>(pa.GetRawText()) : null,
                        false, false, false));
                }
            }

            return new PipelineConfig(
                json.GetProperty("name").GetString() ?? "auto_pipeline",
                json.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                steps);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline generation failed");
            return GetFallbackPipeline(description);
        }
    }

    private static PipelineConfig GetFallbackPipeline(string description)
    {
        var lower = description.ToLowerInvariant();
        var pipeline = ClassificationRegistry.PipelineTrigger.Classify(lower);
        return pipeline switch
        {
            "report_pipeline" => new PipelineConfig("report_pipeline", "Auto-generated report pipeline",
                new() { new(PipelineOp.Extract, null, null, "content", null, false, false, false),
                         new(PipelineOp.Map, "Format as structured report", null, "content", null, true, false, false) }),
            "search_pipeline" => new PipelineConfig("search_pipeline", "Auto-generated search pipeline",
                new() { new(PipelineOp.Extract, null, null, "text", null, false, false, false),
                         new(PipelineOp.Filter, null, "contains(text, 'result')", "text", null, false, false, false) }),
            _ => new PipelineConfig("default_pipeline", "Auto-generated pipeline",
                new() { new(PipelineOp.Extract, null, null, "text", null, false, false, false) })
        };
    }

    private async Task<List<Dictionary<string, object>>> Extract(List<Dictionary<string, object>> data,
        PipelineStep step, Func<string, string, Task<string>>? chatFn)
    {
        var key = step.Key ?? "text";
        foreach (var item in data)
        {
            if (item.TryGetValue(key, out var val) && val is string text)
            {
                var entities = ExtractEntities(text);
                foreach (var (ek, ev) in entities) item[$"extracted_{ek}"] = ev;
            }
        }
        return await Task.FromResult(data);
    }

    private static List<Dictionary<string, object>> Filter(List<Dictionary<string, object>> data, PipelineStep step)
    {
        var cond = step.Condition ?? "";
        return data.Where(item =>
        {
            if (cond.StartsWith("contains("))
            {
                var match = Regex.Match(cond, @"contains\((\w+),\s*'([^']+)'\)");
                if (match.Success)
                {
                    var field = match.Groups[1].Value;
                    var value = match.Groups[2].Value;
                    return item.TryGetValue(field, out var v) && v?.ToString()?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
                }
            }
            return true;
        }).ToList();
    }

    private async Task<List<Dictionary<string, object>>> Map(List<Dictionary<string, object>> data,
        PipelineStep step, Func<string, string, Task<string>>? chatFn)
    {
        if (chatFn == null) return data;
        var key = step.Key ?? "text";
        foreach (var item in data)
        {
            if (item.TryGetValue(key, out var val) && val is string text && text.Length > 10)
            {
                var prompt = $"{step.Prompt ?? "Transform this data"}\nInput: {text[..Math.Min(text.Length, 2000)]}";
                try { item[$"mapped_{key}"] = await chatFn($"map_{key}", prompt); }
                catch { item[$"mapped_{key}"] = text; }
            }
        }
        return data;
    }

    private static List<Dictionary<string, object>> Sort(List<Dictionary<string, object>> data, PipelineStep step)
    {
        var key = step.Key ?? "text";
        return data.OrderBy(item =>
            item.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "").ToList();
    }

    private async Task<List<Dictionary<string, object>>> Reduce(List<Dictionary<string, object>> data,
        PipelineStep step, Func<string, string, Task<string>>? chatFn)
    {
        if (chatFn == null || data.Count < 2) return data;
        var key = step.Key ?? "text";
        var items = string.Join("\n---\n", data.Select(d =>
            d.TryGetValue(key, out var v) ? v?.ToString() ?? "" : ""));
        try
        {
            var summary = await chatFn("reduce", $"Summarize in 3-5 bullet points:\n{items[..Math.Min(items.Length, 4000)]}");
            return new() { new Dictionary<string, object> { [key] = summary, ["count"] = data.Count } };
        }
        catch { return data; }
    }

    private async Task<List<Dictionary<string, object>>> Glean(List<Dictionary<string, object>> data,
        PipelineStep step, Func<string, string, Task<string>>? chatFn)
    {
        if (chatFn == null) return data;
        var key = step.Key ?? "text";
        foreach (var item in data)
        {
            if (item.TryGetValue(key, out var val) && val is string text && text.Length > 20)
            {
                try { item[$"gleaned_{key}"] = await chatFn("glean", $"Refine this: {text[..2000]}"); }
                catch { }
            }
        }
        return data;
    }

    private async Task<List<Dictionary<string, object>>> Resolve(List<Dictionary<string, object>> data, PipelineStep step)
    {
        var key = step.Key ?? "text";
        var seen = new HashSet<string>();
        var resolved = new List<Dictionary<string, object>>();
        foreach (var item in data)
        {
            var hash = item.TryGetValue(key, out var v) ? v?.ToString()?.GetHashCode().ToString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
            if (seen.Add(hash)) resolved.Add(item);
        }
        return await Task.FromResult(resolved);
    }

    private static Dictionary<string, string> ExtractEntities(string text)
    {
        var entities = new Dictionary<string, string>();
        var companyMatch = Regex.Match(text, @"[\u4e00-\u9fff]{2,20}(有限公司|集团|厂|研究院|中心)");
        if (companyMatch.Success) entities["company"] = companyMatch.Value;
        var projectMatch = Regex.Match(text, @"[\u4e00-\u9fff]{2,30}(项目|工程|园区|基地|示范区)");
        if (projectMatch.Success) entities["project"] = projectMatch.Value;
        var numMatch = Regex.Matches(text, @"(\d+(?:\.\d+)?)\s*(吨|千克|mg|km|m|万元|亿元|公顷|亩)");
        if (numMatch.Count > 0) entities["metrics"] = string.Join(", ", numMatch.Take(5).Select(m => m.Value));
        return entities;
    }

    public static string Format(string text) => TextFormatter.Apply(text);

    private static string ExtractJson(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```")) text = text[(text.IndexOf('\n') + 1)..];
        if (text.EndsWith("```")) text = text[..text.LastIndexOf("```")];
        return text;
    }
}
