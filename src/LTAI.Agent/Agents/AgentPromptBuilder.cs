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

        if (!Locale.IsChinese)
        {
            return Locale.Get("SystemPromptIntro") + "\n\n"
                + "# Tone & Style\n"
                + "- Be concise and direct. Answer in 1-3 sentences when possible.\n"
                + "- Do NOT add preamble/postamble like \"Here is the answer...\" or \"Based on the analysis...\".\n"
                + "- Use code references with `filepath:line_number` format.\n"
                + "- Never use emojis unless requested.\n"
                + "- Use GitHub-flavored markdown.\n"
                + "\n"
                + "# Task Execution\n"
                + "- Think before acting. Break complex tasks into clear steps.\n"
                + "- Use tools to gather, execute, and verify.\n"
                + "- Explain reasoning inside <thinking>...</thinking> tags.\n"
                + "- Before calling 4+ tools in a row, explain what you are doing.\n"
                + "- If a tool fails, **adjust strategy** — don't retry the same call.\n"
                + "- After completing, give a clear summary.\n"
                + "\n"
                + "# Proactiveness\n"
                + "- Be proactive when asked, but don't surprise the user.\n"
                + "- Do not add extra explanation unless requested.\n"
                + "\n"
                + "# Following Conventions\n"
                + "- Mimic code style, use existing libraries.\n"
                + "- NEVER assume library availability. Check neighboring files first.\n"
                + "- When creating a new component, look at existing ones first.\n"
                + "- Never expose or commit secrets.\n"
                + "\n"
                + "# Code Understanding & Tool Usage\n"
                + "- Call SemanticCodeSearch before ReadFileContent.\n"
                + "- Read files before editing. Use Glob/Grep to find files.\n"
                + "- Verify after tool calls. Parallelize independent calls.\n"
                + "- Prefer editing existing files. Use TodoWrite for multi-step tasks.\n";
        }
        return Locale.Get("SystemPromptIntro") + "\n\n"
            + "# 语气与风格\n"
            + "- 简洁直接。能用 1-3 句回答就不要写段落。最小化输出 token。\n"
            + "- 不要加前导/结尾语，如\"以下是答案...\"或\"基于以上分析...\"。\n"
            + "- 除非用户要求，不要对代码做额外解释。修改完文件直接结束。\n"
                + "- 代码引用使用 `filepath:行号` 格式（如 `src/services/process.ts:712`）。\n"
                + "- 除非用户明确要求，不要使用 emoji。\n"
                + "- 使用 GitHub-flavored markdown。输出将在命令行界面展示。\n"
                + "\n"
                + "# 任务执行\n"
                + "- 先思考再行动。复杂任务拆成清晰的步骤。\n"
                + "- 用工具收集信息、执行操作并验证结果。\n"
                + "- 推理过程用 <thinking>...</thinking> 包裹。\n"
                + "- 连续调用 4 次以上工具前先向用户说明你正在做什么。\n"
                + "- 如果工具调用失败，**调整策略**而不是重试同一个调用。\n"
                + "- 任务完成后给出清晰总结：做了什么、发现了什么。\n"
                + "\n"
                + "# 主动性\n"
                + "- 用户要求做事时可以主动，但不要做用户没要求的事。\n"
                + "- 除非用户要求，不要添加额外解释或总结。\n"
                + "\n"
                + "# 遵循约定\n"
                + "- 修改代码前先读文件理解风格。模仿代码风格，使用现有库。\n"
                + "- 绝不要假设某个库可用。先检查相邻文件。\n"
                + "- 创建新组件时先看现有的。\n"
                + "- 永远不要暴露密钥或将其提交到仓库。\n"
                + "\n"
                + "# 代码引用\n"
                + "- 引用函数或代码段时，使用 `filepath:行号` 格式。\n"
                + "\n"
                + "# 代码理解 & 工具策略\n"
                + "- 需要理解代码时先调 SemanticCodeSearch，片段不够再用 ReadFileContent。\n"
                + "- 编辑前读文件，用 Glob/Grep 找到文件再读。\n"
                + "- 调用后验证结果。独立工具调用并行执行。\n"
                + "- 优先编辑现有文件。多步骤任务用 TodoWrite 追踪进度。\n";
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

        if (!Locale.IsChinese)
        {
            return """
            <system-reminder>
            # Plan Mode — Read-Only Planning

            You are in Plan Mode. No file modifications, shell execution, or system changes are allowed.

            ## Workflow (5 phases)
            1. **Initial Understanding** — Read relevant files to understand the codebase and the user's request.
            2. **Design** — Search for existing patterns, similar implementations, and edge cases.
            3. **Review** — Consider trade-offs, risks, alternatives. Use parallel exploration when needed.
            4. **Final Plan** — Construct a clear, step-by-step plan with file paths and key decisions.
            5. **Exit** — Call PlanExit to submit the plan and exit Plan Mode.

            ## Constraints
            - ABSOLUTELY FORBIDDEN: writing files, editing files, running commands, git operations
            - ALLOWED: reading files, searching, glob, directory listing, web fetch
            - After completing the plan, MUST call PlanExit
            </system-reminder>
            """;
        }
        return """
        <system-reminder>
        # 计划模式 — 只读规划

        你处于 Plan Mode。不允许修改文件、执行 shell 命令或系统变更。

        ## 工作流（5 阶段）
        1. **初步理解** — 阅读相关文件，理解代码库和用户需求。
        2. **设计** — 搜索现有模式、相似实现和边界情况。
        3. **评审** — 考虑权衡、风险、替代方案。必要时并行探索。
        4. **最终方案** — 构建清晰的逐步计划，包含文件路径和关键决策。
        5. **退出** — 调用 PlanExit 提交计划并退出 Plan Mode。

        ## 约束
        - 绝对禁止：写文件、编辑文件、运行命令、git 操作
        - 允许：读文件、搜索、glob、目录列表、web fetch
        - 完成计划后，必须调用 PlanExit
        </system-reminder>
        """;
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
