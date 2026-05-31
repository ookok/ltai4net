using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using LTAI.AI;

namespace LTAI.Agent.Tools;

[ToolDomain("agent")]
public sealed class AgentGenerator
{
    private readonly IChatClient _llm;

    public AgentGenerator(IChatClient llm)
    {
        _llm = llm;
    }

    [Description("基于用户描述生成新的 Agent 配置。用 LLM 推理出 agent 名称、描述、权限和系统提示词。\n"
        + "适用场景：用户需要一个新的专用助手来处理特定领域任务。\n"
        + "关键参数：description — 用户对 Agent 的自然语言描述。")]
    public async Task<string> GenerateAgent(
        [Description("用户对 Agent 的自然语言描述，如「帮我创建一个代码审查助手，专注安全检查」")] string description)
    {
        var prompt = $$$"""
            You are an AI agent architect. Based on the user's description, generate an agent configuration.

            User request: {{{description}}}

            Generate a JSON object with:
            {
              "name": "lowercase-hyphenated-name",
              "description": "One-line Chinese description",
              "canRead": true/false,
              "canWrite": true/false,
              "canExec": true/false,
              "canList": true/false,
              "temperature": 0.1-1.0,
              "topP": 0.95,
              "systemPrompt": "Detailed instructions for the agent in Chinese"
            }

            Only output the JSON, no other text.
            """;

        try
        {
            var response = await _llm.GetResponseAsync(prompt);
            var text = response.Messages?.LastOrDefault()?.Text ?? "";
            // Extract JSON from potential markdown fences
            if (text.Contains("```"))
            {
                var start = text.IndexOf("```");
                var start2 = text.IndexOf('\n', start);
                var end = text.LastIndexOf("```");
                if (start2 > 0 && end > start2)
                    text = text[(start2 + 1)..end].Trim();
            }
            // Validate JSON
            JsonDocument.Parse(text);
            return $"## Generated Agent Configuration\n\n```json\n{text}\n```\n\n"
                + "将此 JSON 保存到 agents/<name>.agent.md 文件即可注册为新 Agent。";
        }
        catch (Exception ex)
        {
            return $"Generation failed: {ex.Message}";
        }
    }
}
