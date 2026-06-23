using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Pipeline.Steps;

/// <summary>Anti-pattern detected by AntiPatternCheckStep.</summary>
public sealed record AntiPattern(
    string Category,    // "text_cliche", "code_smell", "security", "placeholders"
    string Pattern,     // Short name e.g. "emoji_abuse", "ai_opening"
    string Message,     // Description of the issue
    string Severity,    // "warning", "error"
    string? File = null,
    int Line = 0
);

/// <summary>
/// AntiPatternCheckStep — 反模式检查步骤。
///
/// 受 garden-skills 反 AI 俗套理念启发，在生成后扫描输出内容：
///   - 文本层反模式：AI 常见套话、emoji 滥用、模板残留
///   - 代码层反模式：React 全局样式污染、scrollIntoView、硬编码秘密
///   - 安全模式：硬编码 API key、连接字符串暴露
///
/// 该步骤不会阻断管线，但会注入警告到 agent 上下文供修正。
/// </summary>
public sealed class AntiPatternCheckStep : IPipelineStep
{
    private readonly ILogger<AntiPatternCheckStep> _logger;
    private readonly AntiPatternOptions _options;

    public string Name => "AntiPatternCheck";

    public AntiPatternCheckStep(
        ILogger<AntiPatternCheckStep>? logger = null,
        AntiPatternOptions? options = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AntiPatternCheckStep>.Instance;
        _options = options ?? new AntiPatternOptions();
    }

    public Task<MessageContext> ProcessAsync(MessageContext context)
    {
        var allPatterns = new List<AntiPattern>();

        // ── 扫描最后一条助手消息 ──
        var lastMsg = context.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (lastMsg?.Text != null)
        {
            var textPatterns = ScanTextPatterns(lastMsg.Text);
            allPatterns.AddRange(textPatterns);
        }

        // ── 扫描所有消息中的代码块 ──
        foreach (var msg in context.Messages)
        {
            if (msg.Text == null) continue;
            var codePatterns = ScanCodePatterns(msg.Text);
            allPatterns.AddRange(codePatterns);
        }

        // ── 扫描已写入的文件 ──
        var writtenFiles = ExtractWrittenFiles(context);
        foreach (var filePath in writtenFiles)
        {
            try
            {
                var content = File.ReadAllText(filePath);
                var filePatterns = ScanFilePatterns(filePath, content);
                allPatterns.AddRange(filePatterns);
            }
            catch { /* skip unreadable files */ }
        }

        if (allPatterns.Count == 0)
        {
            _logger.LogDebug("AntiPatternCheck: ✅ no anti-patterns found");
            return Task.FromResult(context);
        }

        context.Set("AntiPatterns", allPatterns);

        var errors = allPatterns.Where(p => p.Severity == "error").ToList();
        if (errors.Count > 0)
        {
            context.AntiPatternBlocked = true;
            var msg = BuildBlockMessage(allPatterns);
            lock (context.MessagesLock) context.Messages.Add(new ChatMessage(ChatRole.System, msg));
            _logger.LogWarning("AntiPatternCheck: ❌ {ErrCount} errors, {TotalCount} total patterns",
                errors.Count, allPatterns.Count);
        }
        else
        {
            var msg = BuildWarningMessage(allPatterns);
            lock (context.MessagesLock) context.Messages.Add(new ChatMessage(ChatRole.System, msg));
            _logger.LogInformation("AntiPatternCheck: ⚠️ {Count} warnings injected",
                allPatterns.Count);
        }

        return Task.FromResult(context);
    }

    // ═══════════════════════════════════════════
    //  文本层反模式扫描
    // ═══════════════════════════════════════════

    private List<AntiPattern> ScanTextPatterns(string text)
    {
        var patterns = new List<AntiPattern>();

        if (text.Length < 10) return patterns;

        // 1. Emoji abuse (decorative, not functional)
        var emojiCount = CountEmoji(text);
        if (emojiCount >= 3)
        {
            patterns.Add(new AntiPattern("text_cliche", "emoji_abuse",
                $"使用了 {emojiCount} 个 emoji 作为装饰，建议减少非必要 emoji（garden-skills: 仅品牌本身使用 emoji 时才可使用）",
                "warning"));
        }

        // 2. AI opening clichés
        if (s_aiOpeningPattern.IsMatch(text))
        {
            patterns.Add(new AntiPattern("text_cliche", "ai_opening",
                "使用了 AI 常用开场白（如 'Let me' / '我来' / 'I\'d be happy to'），建议直接给出结论",
                "warning"));
        }

        // 3. Hedge language (uncertainty qualifiers)
        if (s_hedgePattern.IsMatch(text))
        {
            patterns.Add(new AntiPattern("text_cliche", "hedge_language",
                "包含不确定性表述（'I think' / 'I believe' / '似乎' / 'maybe'），建议确认后直接陈述",
                "warning"));
        }

        // 4. {{template}} placeholders
        if (s_templatePlaceholder.IsMatch(text))
        {
            patterns.Add(new AntiPattern("placeholders", "template_placeholder",
                "包含未替换的 {{模板}} 占位符，请替换为实际内容",
                "error"));
        }

        // 5. TODO/FIXME
        if (s_todoPattern.IsMatch(text))
        {
            patterns.Add(new AntiPattern("placeholders", "todo_fixme",
                "包含 TODO 或 FIXME 未完成标记",
                "warning"));
        }

        // 6. Garden-skills anti-cliché: purple-pink gradient
        if (s_gradientCliche.IsMatch(text))
        {
            patterns.Add(new AntiPattern("text_cliche", "gradient_cliche",
                "紫色/粉色渐变是 AI 生成 UI 的常见俗套，除非品牌规范使用，否则建议避免（garden-skills: 'A brand is recognized, not generic'）",
                "warning"));
        }

        // 7. "I'd be happy to" / "当然可以" AI courtesy overuse
        if (s_courtesyOveruse.IsMatch(text))
        {
            patterns.Add(new AntiPattern("text_cliche", "ai_courtesy",
                "过度客套（'I\'d be happy to' / '当然可以'），建议直接行动，减少客套话",
                "warning"));
        }

        return patterns;
    }

    // ═══════════════════════════════════════════
    //  代码块反模式扫描
    // ═══════════════════════════════════════════

    private static List<AntiPattern> ScanCodePatterns(string text)
    {
        var patterns = new List<AntiPattern>();

        // React: const styles = {...} (global namespace pollution)
        if (s_globalStylesPattern.IsMatch(text))
        {
            patterns.Add(new AntiPattern("code_smell", "global_styles_object",
                "使用 const styles = {{...}} 全局样式对象，多文件会互相覆盖。请使用命名空间如 const headerStyles = {{...}}",
                "error"));
        }

        // scrollIntoView
        if (s_scrollIntoView.IsMatch(text))
        {
            patterns.Add(new AntiPattern("code_smell", "scroll_into_view",
                "使用了 scrollIntoView，在 iframe 嵌入预览环境中会干扰外部框架滚动，建议使用 element.scrollTop 或 window.scrollTo",
                "warning"));
        }

        // Hardcoded localhost URLs
        foreach (Match m in s_hardcodedUrlPattern.Matches(text))
        {
            patterns.Add(new AntiPattern("security", "hardcoded_url",
                $"硬编码 URL '{m.Groups[0].Value}'，建议提取到配置",
                "warning"));
        }

        // Hardcoded API keys / secrets
        if (s_secretPattern.IsMatch(text))
        {
            patterns.Add(new AntiPattern("security", "hardcoded_secret",
                "检测到可能的硬编码密钥或秘密，请使用环境变量或配置管理",
                "error"));
        }

        // Missing null checks in async methods
        if (s_missingNullCheck.IsMatch(text))
        {
            patterns.Add(new AntiPattern("code_smell", "missing_null_check",
                "async 方法中存在未检查 null 的 .Result 或 .Wait() 调用，可能导致死锁或 NullReferenceException",
                "warning"));
        }

        // CSS silhouette substituting for real product imagery
        if (s_cssSilhouette.IsMatch(text))
        {
            patterns.Add(new AntiPattern("code_smell", "css_silhouette",
                "使用 CSS 剪影替代真实产品图片，对于品牌工作应使用真实图片（garden-skills: 'CSS silhouette = 任何品牌都能穿的通用外观'）",
                "warning"));
        }

        return patterns;
    }

    // ═══════════════════════════════════════════
    //  文件级扫描
    // ═══════════════════════════════════════════

    private static List<AntiPattern> ScanFilePatterns(string filePath, string content)
    {
        var patterns = new List<AntiPattern>();
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var lines = content.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;

            // Merge conflict markers
            if (line.StartsWith("<<<<<<< ") || line.StartsWith("=======") || line.StartsWith(">>>>>>> "))
            {
                patterns.Add(new AntiPattern("code_smell", "merge_conflict",
                    "文件包含合并冲突标记，请手动解决冲突",
                    "error", filePath, lineNum));
            }

            // Hardcoded secrets (API keys, passwords, connection strings with credentials)
            if (s_apiKeyPattern.IsMatch(line) && !line.TrimStart().StartsWith("//") && !line.TrimStart().StartsWith("#"))
            {
                patterns.Add(new AntiPattern("security", "hardcoded_api_key",
                    "检测到硬编码 API 密钥或凭据，请使用环境变量或 SecretManager",
                    "error", filePath, lineNum));
            }

            // Empty #region / #endregion
            if (ext == ".cs")
            {
                if (s_emptyRegion.IsMatch(line))
                {
                    patterns.Add(new AntiPattern("code_smell", "empty_region",
                        "空的 #region/#endregion 块，建议移除未使用的代码区域",
                        "warning", filePath, lineNum));
                }
            }

            // Missing CancellationToken in async method signatures
            if (ext == ".cs" && s_asyncMethod.IsMatch(line) && !line.Contains("CancellationToken"))
            {
                if (i + 1 < lines.Length && !lines[i + 1].Contains("CancellationToken") &&
                    i > 0 && !lines[i - 1].Contains("CancellationToken"))
                {
                    patterns.Add(new AntiPattern("code_smell", "missing_cancellation_token",
                        "async 方法缺少 CancellationToken 参数，建议添加以支持取消",
                        "warning", filePath, lineNum));
                }
            }
        }

        return patterns;
    }

    // ═══════════════════════════════════════════
    //  辅助
    // ═══════════════════════════════════════════

    private static int CountEmoji(string text)
    {
        int count = 0;
        foreach (var c in text)
        {
            if ((c >= 0x1F300 && c <= 0x1F9FF) ||
                (c >= 0x2600 && c <= 0x27BF) ||
                (c >= 0xFE00 && c <= 0xFE0F) ||
                c == 0x20E3)
                count++;
        }
        return count;
    }

    private static HashSet<string> ExtractWrittenFiles(MessageContext context)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, args, _) in context.ToolCalls)
        {
            foreach (var path in ExtractPathsFromArgs(name, args))
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    files.Add(path);
            }
        }
        return files;
    }

    private static IEnumerable<string> ExtractPathsFromArgs(string toolName, string args)
    {
        var paths = new List<string>();
        var pathMatches = s_pathArgPattern.Matches(args);
        foreach (Match m in pathMatches)
        {
            if (m.Success)
                paths.Add(m.Groups[1].Value);
        }
        var jsonMatches = s_jsonPathPattern.Matches(args);
        foreach (Match m in jsonMatches)
        {
            if (m.Success)
                paths.Add(m.Groups[1].Value);
        }
        return paths;
    }

    private static string BuildBlockMessage(List<AntiPattern> patterns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## ❌ 反模式检测 — 请修复后继续");
        sb.AppendLine();

        foreach (var group in patterns.GroupBy(p => p.Category))
        {
            sb.AppendLine($"### {group.Key}");
            foreach (var p in group)
            {
                var icon = p.Severity == "error" ? "❌" : "⚠️";
                sb.AppendLine($"  {icon} [{p.Pattern}] {p.Message}");
                if (p.File != null)
                    sb.AppendLine($"      → {p.File}:L{p.Line}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("请使用 edit 工具修复上述问题后继续。");
        return sb.ToString();
    }

    private static string BuildWarningMessage(List<AntiPattern> patterns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## ⚠️ 反模式提示");
        sb.AppendLine();

        foreach (var group in patterns.GroupBy(p => p.Category))
        {
            sb.AppendLine($"**{group.Key}**:");
            foreach (var p in group)
            {
                sb.AppendLine($"- [{p.Pattern}] {p.Message}");
                if (p.File != null)
                    sb.AppendLine($"  → {p.File}:L{p.Line}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("以上为建议性提示，请酌情处理。");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════
    //  编译时常量正则
    // ═══════════════════════════════════════════

    private static readonly Regex s_aiOpeningPattern = new(
        @"##\s*(Let me|让我|我来|让我来|让我先)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_hedgePattern = new(
        @"\b(I think|I believe|I'm not sure|I'm not certain|I guess|maybe|perhaps|似乎|可能吧|maybe it's)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_templatePlaceholder = new(
        @"\{\{.+?\}\}",
        RegexOptions.Compiled);

    private static readonly Regex s_todoPattern = new(
        @"\b(TODO|FIXME|HACK|XXX|WORKAROUND)\b",
        RegexOptions.Compiled);

    private static readonly Regex s_gradientCliche = new(
        @"(purple|pink|violet|magenta).*(gradient|渐变|degrade)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_courtesyOveruse = new(
        @"\b(I'd be happy to|I'd be glad to|当然可以|没问题让我|请放心)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_globalStylesPattern = new(
        @"const\s+styles\s*=\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex s_scrollIntoView = new(
        @"\.scrollIntoView\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex s_hardcodedUrlPattern = new(
        @"https?://(?:localhost|127\.0\.0\.1)[^\s""'》<>]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_secretPattern = new(
        @"(?:api[_-]?key|apikey|secret|password|token|access[_-]?key)\s*[:=]\s*['""][A-Za-z0-9_\-]{16,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_missingNullCheck = new(
        @"\.Result\b(?!\s*\?)",
        RegexOptions.Compiled);

    private static readonly Regex s_cssSilhouette = new(
        @"(?:clip[-]?path|polygon).*(?:silhouette|剪影|轮廓)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_apiKeyPattern = new(
        @"['""](?:sk-[-_a-zA-Z0-9]{20,}|AIza[0-9A-Za-z\-_]{35}|ghp_[0-9a-zA-Z]{36}|Bearer\s+[A-Za-z0-9_\-]{20,})['""]",
        RegexOptions.Compiled);

    private static readonly Regex s_emptyRegion = new(
        @"#region\s*\r?\n\s*#endregion",
        RegexOptions.Compiled);

    private static readonly Regex s_asyncMethod = new(
        @"\basync\s+(?:Task|ValueTask|IAsyncEnumerable)\b",
        RegexOptions.Compiled);

    private static readonly Regex s_pathArgPattern = new(
        @"(?:path|filePath|file)\s*=\s*""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_jsonPathPattern = new(
        @"""(?:path|filePath|file)""\s*:\s*""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
}

public sealed class AntiPatternOptions
{
    public bool EnableTextClicheCheck { get; set; } = true;
    public bool EnableCodeSmellCheck { get; set; } = true;
    public bool EnableSecurityCheck { get; set; } = true;
}
