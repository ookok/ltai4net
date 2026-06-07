using System.Text.RegularExpressions;

namespace LTAI.TUI;

public sealed record ConfirmRequestInfo(string Title, string Message, string ExtraInfo);

/// <summary>
/// Detects tool confirmation requests from AI responses.
/// Recognizes patterns: shell command confirm, out-of-workspace paths,
/// file download, env var set, out-of-workspace file edit, generic safety.
/// </summary>
public static class ConfirmRequestParser
{
    public static (string title, string message, string extraInfo)? Parse(string text)
    {
        if (TryParse(text, out var info))
            return (info.Title, info.Message, info.ExtraInfo);
        return null;
    }

    public static bool TryParse(string text, out ConfirmRequestInfo info)
    {
        info = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var shellMatch = Regex.Match(text,
            @"⚠️\s*需要.*(?:shell|命令).*确认.*\n命令:\s*`([^`]+)`.*\n目录:\s*(.+)",
            RegexOptions.Singleline);
        if (shellMatch.Success)
        {
            info = new ConfirmRequestInfo(
                "执行 Shell 命令",
                shellMatch.Groups[1].Value.Trim(),
                $"目录: {shellMatch.Groups[2].Value.Trim()}");
            return true;
        }

        var pathMatch = Regex.Match(text,
            @"⚠️.*路径在工作区外:\s*`([^`]+)`",
            RegexOptions.Singleline);
        if (pathMatch.Success)
        {
            info = new ConfirmRequestInfo(
                "访问工作区外路径",
                pathMatch.Groups[1].Value.Trim(),
                "路径在项目工作区之外，需要授权才能访问");
            return true;
        }

        if (text.Contains("需要下载文件") && text.Contains("确认"))
        {
            var urlMatch = Regex.Match(text, @"https?://[^\s""'<>]+");
            var url = urlMatch.Success ? urlMatch.Value : "(未指定)";
            info = new ConfirmRequestInfo("下载文件", url, "需要用户确认后才能下载外部文件");
            return true;
        }

        if (text.Contains("设置环境变量") && text.Contains("确认"))
        {
            info = new ConfirmRequestInfo("设置环境变量", "环境变量操作", "修改环境变量可能影响系统行为");
            return true;
        }

        var editMatch = Regex.Match(text,
            @"需要编辑工作区外的文件.*?目标路径:\s*`([^`]+)`",
            RegexOptions.Singleline);
        if (editMatch.Success)
        {
            info = new ConfirmRequestInfo(
                "编辑文件",
                editMatch.Groups[1].Value.Trim(),
                "文件在项目工作区之外");
            return true;
        }

        if (text.Contains("⚠️") && text.Contains("确认"))
        {
            var firstLine = text.Split('\n')[0].Trim();
            info = new ConfirmRequestInfo(
                "安全确认",
                firstLine.Replace("⚠️", "").Trim(),
                "详情按 D 键查看");
            return true;
        }

        return false;
    }
}
