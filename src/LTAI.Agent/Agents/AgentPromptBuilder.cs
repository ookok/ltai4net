using LTAI.Core.I18n;
using LTAI.Agent.Prompts;

namespace LTAI.Agent;

/// <summary>
/// Bilingual system prompt + plan-mode + agent description builder.
/// All strings route through <see cref="Locale"/> so the agent speaks the user's OS language.
/// Prompts loaded from agents/*.prompt.md override built-in strings when present.
/// </summary>
internal static class AgentPromptBuilder
{
    public static string BuildSystemPrompt()
    {
        var lang = Locale.IsChinese ? "zh" : "en";
        var filePrompt = PromptLoader.Load($"system-{lang}");
        if (!string.IsNullOrEmpty(filePrompt))
            return filePrompt;

        // Minimal emergency fallback (shouldn't be reached when agents/system-*.prompt.md exists)
        return Locale.IsChinese
            ? "你是 LTAI Assistant，基于 Microsoft Agent Framework 的多 agent 协作系统。"
            : "You are LTAI Assistant, a multi-agent collaboration system on Microsoft Agent Framework.";
    }

    public static string AppendAgentPrompt(string basePrompt, string? agentPrompt)
    {
        if (string.IsNullOrWhiteSpace(agentPrompt)) return basePrompt;
        return basePrompt + "\n\n## Agent 指令\n" + agentPrompt.Trim();
    }

    public static string BuildPlanModePrompt()
    {
        var lang = Locale.IsChinese ? "zh" : "en";
        var filePrompt = PromptLoader.Load($"plan-mode-{lang}");
        if (!string.IsNullOrEmpty(filePrompt))
            return filePrompt;

        // Minimal emergency fallback
        return Locale.IsChinese
            ? "# 计划模式 — 只读规划\n\n你处于 Plan Mode。不允许修改文件、执行 shell 命令或系统变更。\n完成后必须调用 PlanExit。"
            : "# Plan Mode — Read-Only Planning\n\nYou are in Plan Mode. No file modifications, shell execution, or system changes are allowed.\nMust call PlanExit when done.";
    }

    public static string BuildAgentDescription(string name, string description)
    {
        var isEn = !Locale.IsChinese;
        var roleLine = isEn
            ? $"You are {name}, {description}."
            : $"你是 {name}，{description}。";
        var dateHint = isEn
            ? "About dates: when users ask \"what day is it\" or \"what time is it\", call GetCurrentDateTime directly — do not guess."
            : "关于日期：当用户询问\"今天星期几\"\"现在几点\"等时间日期问题时，请直接调用 GetCurrentDateTime 工具获取实时时间，不要自行估算。";
        return $"{roleLine}\n{dateHint}\n";
    }
}
