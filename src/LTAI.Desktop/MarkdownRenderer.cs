using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace LTAI.Desktop;

public static class MarkdownRenderer
{
    public static InlineCollection Render(string text, InlineCollection inlines)
    {
        var lines = text.Split('\n');
        for (int li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            if (li > 0) inlines.Add(new LineBreak());

            if (line.StartsWith("### "))
                inlines.Add(Run(line[4..], LtaiTheme.TextPrimary, weight: FontWeight.Bold, fontSize: 15));
            else if (line.StartsWith("## "))
                inlines.Add(Run(line[3..], LtaiTheme.TextPrimary, weight: FontWeight.Bold, fontSize: 17));
            else if (line.StartsWith("# "))
                inlines.Add(Run(line[2..], LtaiTheme.TextPrimary, weight: FontWeight.Bold, fontSize: 19));
            else if (line.StartsWith("> "))
                inlines.Add(Run(line, LtaiTheme.TextDim, style: FontStyle.Italic));
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                inlines.Add(Run("  ", LtaiTheme.TextSecondary));
                inlines.Add(Run("\u2022 ", LtaiTheme.AccentDNA));
                RenderInline(line[2..], inlines);
            }
            else if (line.StartsWith("```"))
                inlines.Add(Run(line, LtaiTheme.TextDim, style: FontStyle.Italic));
            else
                RenderInline(line, inlines);
        }
        return inlines;
    }

    public static InlineCollection RenderInline(string text, InlineCollection inlines)
    {
        var i = 0;
        while (i < text.Length)
        {
            var codeEnd = text.IndexOf('`', i);
            var boldEnd = text.IndexOf("**", i);
            var italicEnd = text.IndexOf('*', i);

            if (codeEnd >= i && (boldEnd < i || codeEnd <= boldEnd) && (italicEnd < i || codeEnd <= italicEnd))
            {
                if (codeEnd > i) inlines.Add(Run(text[i..codeEnd]));
                var end = text.IndexOf('`', codeEnd + 1);
                if (end > codeEnd)
                {
                    inlines.Add(Run(text[(codeEnd + 1)..end], LtaiTheme.AccentInfo, style: FontStyle.Italic));
                    i = end + 1;
                }
                else { inlines.Add(Run(text[codeEnd..])); break; }
            }
            else if (boldEnd >= i && (italicEnd < i || boldEnd <= italicEnd))
            {
                if (boldEnd > i) inlines.Add(Run(text[i..boldEnd]));
                var end = text.IndexOf("**", boldEnd + 2);
                if (end > boldEnd)
                {
                    inlines.Add(Run(text[(boldEnd + 2)..end], LtaiTheme.TextPrimary, FontWeight.Bold));
                    i = end + 2;
                }
                else { inlines.Add(Run(text[boldEnd..])); break; }
            }
            else if (codeEnd >= i && codeEnd < text.Length)
            {
                if (codeEnd > i) inlines.Add(Run(text[i..codeEnd]));
                var end = text.IndexOf('`', codeEnd + 1);
                if (end > codeEnd)
                {
                    inlines.Add(CodeRun(text[(codeEnd + 1)..end]));
                    i = end + 1;
                }
                else { inlines.Add(Run(text[codeEnd..])); break; }
            }
            else if (italicEnd >= i)
            {
                if (italicEnd > i) inlines.Add(Run(text[i..italicEnd]));
                var end = text.IndexOf('*', italicEnd + 1);
                if (end > italicEnd)
                {
                    inlines.Add(Run(text[(italicEnd + 1)..end], LtaiTheme.TextPrimary, style: FontStyle.Italic));
                    i = end + 1;
                }
                else { inlines.Add(Run(text[italicEnd..])); break; }
            }
            else
            {
                inlines.Add(Run(text[i..]));
                break;
            }
        }
        return inlines;
    }

    private static Run Run(string text, Color? color = null, FontWeight? weight = null, double? fontSize = null, FontStyle? style = null)
    {
        var run = new Run { Text = text };
        if (color.HasValue) run.Foreground = LtaiTheme.Sbb(color.Value);
        else run.Foreground = LtaiTheme.Sbb(LtaiTheme.TextPrimary);
        if (weight.HasValue) run.FontWeight = weight.Value;
        if (fontSize.HasValue) run.FontSize = fontSize.Value;
        if (style.HasValue) run.FontStyle = style.Value;
        return run;
    }

    private static Run CodeRun(string text) => new() { Text = text, Foreground = LtaiTheme.Sbb(LtaiTheme.AccentInfo), FontFamily = new("Consolas") };
}
