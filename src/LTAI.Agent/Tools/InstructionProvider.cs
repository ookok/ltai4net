using LTAI.Core.I18n;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Tools;

public sealed class InstructionProvider : AIContextProvider
{
    private static string? _cachedAgentsMd;
    private static readonly object _agentsMdLock = new();
    private static FileSystemWatcher? _agentsMdWatcher;
    private static string? _watchedPath;
    private readonly string? _modelId;

    /// <summary>Invalidate the AGENTS.md cache so next call reloads from disk (thread-safe).</summary>
    public static void InvalidateCache()
    {
        lock (_agentsMdLock)
        {
            _cachedAgentsMd = null;
        }
    }

    /// <summary>Dispose the FileSystemWatcher if started.</summary>
    public static void StopWatching()
    {
        if (_agentsMdWatcher != null)
        {
            _agentsMdWatcher.EnableRaisingEvents = false;
            _agentsMdWatcher.Dispose();
            _agentsMdWatcher = null;
        }
    }

    /// <summary>Initialize FileSystemWatcher to auto-invalidate on file changes.</summary>
    public static void StartWatching()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "AGENTS.md"),
            Path.Combine(Directory.GetCurrentDirectory(), "AGENTS.md"),
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            var dir = Path.GetDirectoryName(path);
            if (dir == null) return;
            _watchedPath = path;
            _agentsMdWatcher = new FileSystemWatcher(dir, "AGENTS.md")
            {
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
            };
            _agentsMdWatcher.Changed += (_, _) => InvalidateCache();
            _agentsMdWatcher.Created += (_, _) => InvalidateCache();
            _agentsMdWatcher.Deleted += (_, _) => InvalidateCache();
            _agentsMdWatcher.Renamed += (_, _) => InvalidateCache();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => StopWatching();
            return;
        }
    }

    public InstructionProvider(string? modelId = null) : base(null, null, null)
    {
        _modelId = modelId;
    }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        var rules = BuildRules();

        var agentsMd = LoadAgentsMd();
        if (!string.IsNullOrEmpty(agentsMd))
            rules += $"\n\n[项目指令]\n{agentsMd}";

        var msg = new ChatMessage(ChatRole.System, rules);

        var msgs = context.AIContext?.Messages?.ToList() ?? [];
        msgs.Insert(0, msg);

        var instructions = context.AIContext?.Instructions ?? "";
        var modelGuidance = BuildModelGuidance();
        if (!string.IsNullOrEmpty(modelGuidance))
            instructions = string.IsNullOrEmpty(instructions)
                ? modelGuidance
                : instructions + "\n\n" + modelGuidance;

        return ValueTask.FromResult(new AIContext
        {
            Instructions = instructions,
            Messages = msgs,
        });
    }

    private string BuildModelGuidance()
    {
        if (string.IsNullOrEmpty(_modelId)) return "";

        if (_modelId.Contains("pro", StringComparison.OrdinalIgnoreCase) ||
            _modelId.Contains("deepseek-reasoner", StringComparison.OrdinalIgnoreCase))
        {
            return "[模型提示]\n"
                 + "你运行在深度推理模式下（Pro / DeepSeek-Reasoner）。\n"
                 + "- 逐步推理：把复杂问题分解为子问题，逐步解决。\n"
                 + "- 展示完整的 CoT（Chain-of-Thought），确保每一步可验证。\n"
                 + "- 输出用 <thinking>...</thinking> 包裹推理过程。\n"
                 + "- 代码修改前先分析影响范围。";
        }

        if (_modelId.Contains("flash", StringComparison.OrdinalIgnoreCase) ||
            _modelId.Contains("fast", StringComparison.OrdinalIgnoreCase))
        {
            return "[模型提示]\n"
                 + "你运行在快速响应模式下（Flash / Fast）。\n"
                 + "- 优先使用工具获取信息，不要仅凭训练数据回答实时问题。\n"
                 + "- 回答要简洁、直接，只在必要时展示推理过程。\n"
                 + "- 复杂问题用 <thinking>...</thinking> 快速概括思路，然后直接输出答案。";
        }

        return "";
    }

    private static string BuildRules()
    {
        return Locale.IsChinese
            ? "[操作规则]\n"
            + "1. 当工具返回「尚未获得权限」时，向用户展示路径并询问是否允许。MAF ToolApprovalAgent 会拦截工具调用进行审批。\n"
            + "2. 不要尝试其他工具代替未授权的操作。\n"
            + "3. 参数必须是正确的JSON类型（数字不要加引号，布尔值用true/false）。\n"
            + "4. 不要用Markdown代码块包围工具调用。\n"
            + "5. 如果工具调用失败，先检查参数再重试，不要重复调用同一个工具。"
            : "[Operation Rules]\n"
            + "1. When a tool returns 'no permission', show the path to the user and ask for approval. MAF ToolApprovalAgent will intercept tool calls for approval.\n"
            + "2. Do not attempt alternative tools to bypass unauthorized operations.\n"
            + "3. Parameters must be correct JSON types (numbers without quotes, booleans as true/false).\n"
            + "4. Do not wrap tool calls in Markdown code blocks.\n"
            + "5. If a tool call fails, check parameters before retrying — do not call the same tool repeatedly.";
    }

    private string? LoadAgentsMd()
    {
        if (_cachedAgentsMd != null) return _cachedAgentsMd;
        lock (_agentsMdLock)
        {
            if (_cachedAgentsMd != null) return _cachedAgentsMd;

            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "AGENTS.md"),
                Path.Combine(Directory.GetCurrentDirectory(), "AGENTS.md"),
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        var content = File.ReadAllText(path);
                        _cachedAgentsMd = FilterAgentsMd(content);
                    }
                    catch { }
                    break;
                }
            }
        }
        return _cachedAgentsMd;
    }

    /// <summary>
    /// P7: 裁剪 AGENTS.md 只保留对 LLM 有用的架构决策和关键约定，
    /// 跳过 P1-P16 阶段规划、排期表、文件清单等噪音。
    /// </summary>
    private static string FilterAgentsMd(string content)
    {
        var lines = content.Split('\n');
        var relevant = new List<string>();
        var inPlanning = false;
        var linesKept = 0;
        const int MaxLines = 30;

        foreach (var line in lines)
        {
            if (linesKept >= MaxLines) break;

            // Keep: 开头的Title, Goal, 审查结论, 关键决策 section
            var trimmed = line.TrimStart();
            if (IsYearHeading(trimmed) || trimmed.StartsWith("## Goal")
                || trimmed.StartsWith("## 审查结论") || trimmed.StartsWith("## 关键决策")
                || trimmed.StartsWith("- **D"))
            {
                relevant.Add(line);
                linesKept++;
                inPlanning = false;
                continue;
            }

            // Skip: Files to touch / Plan tables / P1-P16 sections / Verification sections
            if (trimmed.StartsWith("## Files to touch") || trimmed.StartsWith("## 计划")
                || trimmed.StartsWith("## 依赖关系") || trimmed.StartsWith("## Verification")
                || trimmed.StartsWith("## 验证") || trimmed.StartsWith("### P")
                || trimmed.StartsWith("| 阶段 |") || trimmed.StartsWith("|---"))
            {
                inPlanning = true;
                continue;
            }
            if (trimmed.StartsWith("### ") && trimmed.Contains("P"))
            {
                inPlanning = true;
                continue;
            }
            // Re-entry after planning block
            if (trimmed.StartsWith("## ") && inPlanning)
            {
                inPlanning = false;
                // Resume from next iteration; don't add this line yet
            }

            if (inPlanning) continue;

            // Keep general decision / architecture lines
            if (!string.IsNullOrWhiteSpace(line) && linesKept > 0)
            {
                relevant.Add(line);
                linesKept++;
            }
        }

        return string.Join("\n", relevant);
    }

    private static bool IsYearHeading(string trimmed)
    {
        // Match "# YYYY-" or "# YYYY年" headings (dynamic year, not hardcoded)
        if (trimmed.Length < 7 || trimmed[0] != '#') return false;
        var afterHash = trimmed.AsSpan(1).TrimStart();
        if (afterHash.Length < 5) return false;
        for (int i = 0; i < 4; i++)
        {
            if (!char.IsDigit(afterHash[i])) return false;
        }
        return afterHash.Length > 4 && (afterHash[4] == '-' || afterHash[4] == '年');
    }
}
