using System.Text;

namespace LTAI.Agent.CodeGeneration;

public enum CodeOutputFormat
{
    FullFile,
    BlockDiff,
    FuncDiff,
}

public sealed record CodeEditContext(
    string FilePath,
    int FileLineCount,
    bool EditsAreLocalized,
    int EditRegionLineCount,
    string OriginalContent,
    string NewContent);

public sealed class AdaptiveCodeOutputFormatter
{
    public CodeOutputFormat SelectFormat(CodeEditContext context)
    {
        if (context.FileLineCount < 100)
            return CodeOutputFormat.FullFile;

        if (context.EditsAreLocalized && context.EditRegionLineCount < 30)
            return CodeOutputFormat.FuncDiff;

        return CodeOutputFormat.BlockDiff;
    }

    public string Format(CodeEditContext context)
    {
        var format = SelectFormat(context);
        return format switch
        {
            CodeOutputFormat.FullFile => context.NewContent,
            CodeOutputFormat.FuncDiff => BuildFuncDiff(context),
            CodeOutputFormat.BlockDiff => BuildBlockDiff(context),
            _ => context.NewContent,
        };
    }

    private static string BuildBlockDiff(CodeEditContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"```diff");
        sb.AppendLine($"# {context.FilePath}");

        var origLines = context.OriginalContent.Split('\n');
        var newLines = context.NewContent.Split('\n');
        var maxLen = Math.Max(origLines.Length, newLines.Length);

        for (int i = 0; i < maxLen; i++)
        {
            var orig = i < origLines.Length ? origLines[i].TrimEnd('\r') : "";
            var next = i < newLines.Length ? newLines[i].TrimEnd('\r') : "";

            if (orig != next)
            {
                if (!string.IsNullOrEmpty(orig))
                    sb.AppendLine("- " + orig);
                if (!string.IsNullOrEmpty(next))
                    sb.AppendLine("+ " + next);
            }
        }

        sb.AppendLine("```");
        return sb.ToString();
    }

    private static string BuildFuncDiff(CodeEditContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"```diff");
        sb.AppendLine($"# {context.FilePath} (局部修改)");

        var origLines = context.OriginalContent.Split('\n');
        var newLines = context.NewContent.Split('\n');
        var startLine = Math.Max(0, context.EditRegionLineCount > 0
            ? FindDiffStart(origLines, newLines)
            : 0);
        var endLine = Math.Min(newLines.Length - 1,
            startLine + context.EditRegionLineCount + 5);

        sb.AppendLine($"@@ -{startLine + 1},{endLine - startLine + 1} @@");
        for (int i = startLine; i <= endLine && i < origLines.Length && i < newLines.Length; i++)
        {
            var orig = origLines[i].TrimEnd('\r');
            var next = newLines[i].TrimEnd('\r');
            if (orig != next)
            {
                sb.AppendLine("-" + orig);
                sb.AppendLine("+" + next);
            }
            else
            {
                sb.AppendLine(" " + orig);
            }
        }

        sb.AppendLine("```");
        return sb.ToString();

        static int FindDiffStart(string[] a, string[] b)
        {
            var min = Math.Min(a.Length, b.Length);
            for (int i = 0; i < min; i++)
                if (a[i] != b[i]) return Math.Max(0, i - 2);
            return 0;
        }
    }
}
