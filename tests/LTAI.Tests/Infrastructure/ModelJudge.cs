using Microsoft.Extensions.AI;
using System.Text.Json;

namespace LTAI.Tests;

public sealed record CritiqueCriteria
{
    public bool HasCompilableCode { get; init; }
    public bool NoHallucinations { get; init; }
    public bool ProvidesSolution { get; init; }
    public bool NoFailureSignals { get; init; }
    public double PassThreshold { get; init; } = 0.7;
}

public sealed class ModelJudge
{
    private readonly IChatClient _judgeLlm;
    public ModelJudge(IChatClient judgeLlm) => _judgeLlm = judgeLlm;

    public async Task<JudgeVerdict> EvaluateAsync(string response, CritiqueCriteria criteria, CancellationToken ct = default)
    {
        var prompt = BuildPrompt(response, criteria);
        var chatResponse = await _judgeLlm.GetResponseAsync([
            new ChatMessage(ChatRole.System, prompt),
        ], cancellationToken: ct);
        var text = chatResponse.Text ?? "";
        return ParseVerdict(text, criteria);
    }

    private static string BuildPrompt(string response, CritiqueCriteria c)
    {
        var checks = new List<string>();
        if (c.HasCompilableCode) checks.Add("- 如果包含代码，应语法正确、可编译");
        if (c.NoHallucinations) checks.Add("- 不应包含虚构的事实、标准编号或引用");
        if (c.ProvidesSolution) checks.Add("- 应提供具体解决方案，而非仅描述问题");
        if (c.NoFailureSignals) checks.Add("- 不应包含拒绝回答或失败信号");

        return "你是一个严格的质量评估员。分析以下 AI 回复，按标准逐项评分。"
            + "\n\n评估标准：\n" + string.Join("\n", checks)
            + "\n\n回复内容：\n---\n" + response + "\n---\n"
            + "\n\n请以 JSON 格式回复：{\"scores\":{\"item1\":0/1},\"reason\":\"总结\",\"pass\":true/false}";
    }

    private static JudgeVerdict ParseVerdict(string text, CritiqueCriteria criteria)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(text);
            var pass = json.TryGetProperty("pass", out var p) && p.GetBoolean();
            var reason = json.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            return new JudgeVerdict(pass, reason, criteria.PassThreshold);
        }
        catch
        {
            return new JudgeVerdict(false, "Parse error: " + text[..Math.Min(100, text.Length)], criteria.PassThreshold);
        }
    }
}

public sealed record JudgeVerdict(bool Pass, string Reason, double Threshold);
