using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI;

public sealed class DiffViewer
{
    public IRenderable Render(string oldText, string newText, string? title = null)
    {
        var diff = DiffEngine.Compute(oldText, newText);

        var grid = new Grid();

        if (IsSideBySidePreferred(diff))
        {
            grid.AddColumns(2);
            grid.AddRow(
                BuildSidePanel(diff, isOld: true),
                BuildSidePanel(diff, isOld: false));
        }
        else
        {
            grid.AddColumn();
            grid.AddRow(BuildUnifiedPanel(diff));
        }

        var panel = new Panel(grid)
        {
            Header = new PanelHeader(title ?? "[green]Diff[/]"),
            Border = BoxBorder.Rounded
        };
        panel.BorderColor(Color.Grey);
        return panel;
    }

    private static bool IsSideBySidePreferred(DiffResult diff) =>
        diff.OriginalLines.Length < 40 && diff.ModifiedLines.Length < 40;

    private static IRenderable BuildSidePanel(DiffResult diff, bool isOld)
    {
        var sb = new System.Text.StringBuilder();
        var label = isOld ? "[red]--- Original[/]" : "[green]+++ Modified[/]";
        sb.AppendLine(label);
        sb.AppendLine();

        var edits = diff.Edits;

        for (var i = 0; i < edits.Count; i++)
        {
            var e = edits[i];

            if (isOld && e.Type is DiffType.Removed)
                sb.AppendLine($"[red]{e.OldLine + 1,4} -[/] [red]{Escape(e.OldText)}[/]");
            else if (isOld && e.Type is DiffType.Changed)
                sb.AppendLine($"[red]{e.OldLine + 1,4} -[/] [red]{Escape(e.OldText)}[/]");
            else if (isOld && e.Type is DiffType.Unchanged)
                sb.AppendLine($"[grey]{e.OldLine + 1,4}  [/] [grey]{Escape(e.OldText)}[/]");
            else if (!isOld && e.Type is DiffType.Added)
                sb.AppendLine($"[green]{e.NewLine + 1,4} +[/] [green]{Escape(e.NewText)}[/]");
            else if (!isOld && e.Type is DiffType.Changed)
                sb.AppendLine($"[green]{e.NewLine + 1,4} +[/] [green]{Escape(e.NewText)}[/]");
            else if (!isOld && e.Type is DiffType.Unchanged)
                sb.AppendLine($"[grey]{e.NewLine + 1,4}  [/] [grey]{Escape(e.NewText)}[/]");
        }

        return new Markup(sb.ToString().TrimEnd());
    }

    private static IRenderable BuildUnifiedPanel(DiffResult diff)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[green]+{diff.AddedCount}[/] [red]-{diff.RemovedCount}[/] [grey]{diff.Edits.Count(e => e.Type == DiffType.Unchanged)} unchanged[/]");
        sb.AppendLine();

        var edits = diff.Edits;
        for (var i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            var oldNum = e.OldLine >= 0 ? (e.OldLine + 1).ToString().PadLeft(4) : "    ";
            var newNum = e.NewLine >= 0 ? (e.NewLine + 1).ToString().PadLeft(4) : "    ";

            switch (e.Type)
            {
                case DiffType.Added:
                    sb.AppendLine($"[green]{oldNum} {newNum} +[/] [green]{Escape(e.NewText)}[/]");
                    break;
                case DiffType.Removed:
                    sb.AppendLine($"[red]{oldNum} {newNum} -[/] [red]{Escape(e.OldText)}[/]");
                    break;
                case DiffType.Changed:
                    sb.AppendLine($"[red]{oldNum} {newNum} -[/] [red]{Escape(e.OldText)}[/]");
                    sb.AppendLine($"[green]{oldNum} {newNum} +[/] [green]{Escape(e.NewText)}[/]");
                    break;
                case DiffType.Unchanged:
                    sb.AppendLine($"[grey]{oldNum} {newNum}  {Escape(e.OldText)}[/]");
                    break;
            }
        }

        return new Markup(sb.ToString().TrimEnd());
    }

    private static string Escape(string t) => t.Replace("[", "[[").Replace("]", "]]");
}
