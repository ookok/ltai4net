using System.Text;
using LTAI.Agent.Tools;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace LTAI.TUI;

public sealed class QuestionFormView
{
    private readonly Layout _layout;
    private readonly LiveDisplayContext _liveCtx;
    private readonly QuestionService _questionService;
    private readonly Action<string, string> _updateFooter;

    public QuestionFormView(
        Layout layout,
        LiveDisplayContext liveCtx,
        QuestionService questionService,
        Action<string, string> updateFooter)
    {
        _layout = layout;
        _liveCtx = liveCtx;
        _questionService = questionService;
        _updateFooter = updateFooter;
    }

    public async Task ShowAsync(QuestionPost post, CancellationToken ct)
    {
        var answers = new List<IReadOnlyList<string>>();

        for (int i = 0; i < post.Questions.Count; i++)
        {
            var q = post.Questions[i];
            var chosen = await ShowSingleQuestionAsync(q, i, post.Questions.Count, ct);
            answers.Add(chosen);
        }

        _questionService.Reply(post.RequestId, answers);
    }

    private async Task<IReadOnlyList<string>> ShowSingleQuestionAsync(QuestionPrompt q, int idx, int total, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            RenderQuestion(q, idx, total);

            if (q.Options.Count > 0)
            {
                return q.Multiple
                    ? HandleMultiChoice(q, idx, total)
                    : HandleSingleChoice(q, idx, total);
            }

            return new[] { ShowTextInputInline(q, idx, total) };
        }, ct);
    }

    private void RenderQuestion(QuestionPrompt q, int idx, int total)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"[yellow]── ❓ 问题 {idx + 1}/{total} ──[/]");
        sb.AppendLine($"[bold]{q.Header.EscapeMarkup()}[/]");
        sb.AppendLine($"[grey]{q.Question.EscapeMarkup()}[/]");
        sb.AppendLine();

        if (q.Options.Count > 0)
        {
            for (int j = 0; j < q.Options.Count; j++)
            {
                var opt = q.Options[j];
                var key = q.Multiple ? $"[{j + 1}]" : $"{(char)('a' + j)}";
                sb.AppendLine($"  [cyan]{key}[/] {opt.Label.EscapeMarkup()}");
                if (!string.IsNullOrEmpty(opt.Description))
                    sb.AppendLine($"     [dim]{opt.Description.EscapeMarkup()}[/]");
            }
            sb.AppendLine();
            sb.AppendLine(q.Multiple
                ? "[grey]输入序号（逗号分隔多选, Enter 确认, c=自定义回答）: [/]"
                : "[grey]输入字母选择 (a/b/c..., c=自定义, Enter 确认): [/]");
        }
        else
        {
            sb.AppendLine("[grey]输入回答 (Enter 确认): [/]");
        }

        lock (_layout)
        {
            _layout["Messages"].Update(new Panel(sb.ToString().TrimEnd()).Border(BoxBorder.Rounded).Expand());
            _updateFooter("", $"[yellow]❓ 问题 {idx + 1}/{total}[/]");
            _liveCtx.Refresh();
        }
    }

    private IReadOnlyList<string> HandleSingleChoice(QuestionPrompt q, int idx, int total)
    {
        var options = q.Options.Select(o => o.Label).ToArray();
        var prompt = new SelectionPrompt<string>()
            .Title($"[yellow]选择:[/]")
            .PageSize(10)
            .AddChoices(options)
            .MoreChoicesText("[grey](滚动查看更多)[/]");

        // Allow custom input by adding a special option
        if (q.Options.Any(o => o.Label == "自定义回答" || o.Label == "Custom"))
        {
            // Already in options - just use it
        }

        AnsiConsole.WriteLine();
        var choice = AnsiConsole.Prompt(prompt);
        return new[] { choice };
    }

    private IReadOnlyList<string> HandleMultiChoice(QuestionPrompt q, int idx, int total)
    {
        var chosen = new List<string>();
        var options = q.Options.Select(o => o.Label).ToArray();

        AnsiConsole.MarkupLine("[grey]输入序号（逗号分隔多选, Enter 确认, c=自定义回答）: [/]");
        while (true)
        {
            var input = Console.ReadLine() ?? "";
            if (string.IsNullOrWhiteSpace(input)) break;

            if (input.Trim().ToLowerInvariant() == "c")
            {
                chosen.Clear();
                chosen.Add(ShowTextInputInline(q, idx, total));
                break;
            }

            var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var p in parts)
            {
                if (int.TryParse(p, out var num) && num >= 1 && num <= options.Length)
                    chosen.Add(options[num - 1]);
            }

            if (chosen.Count > 0) break;
            AnsiConsole.MarkupLine("[yellow]无效选择，请重试[/]");
        }

        return chosen.ToArray();
    }

    private string ShowTextInputInline(QuestionPrompt q, int idx, int total)
    {
        lock (_layout)
        {
            _updateFooter("", $"[yellow]✏️ 问题 {idx + 1}/{total}: {q.Header.EscapeMarkup()}[/]");
            _liveCtx.Refresh();
        }
        AnsiConsole.Markup("[grey]输入回答: [/]");
        return Console.ReadLine() ?? "(跳过)";
    }
}
