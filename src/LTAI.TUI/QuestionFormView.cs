using LTAI.Agent.Tools;

namespace LTAI.TUI;

/// <summary>Handles interactive questions from the LLM via overlay mode.</summary>
public sealed class QuestionFormView
{
    private readonly ChatLayout _host;
    private readonly QuestionService _questionService;

    public QuestionFormView(ChatLayout host, QuestionService questionService)
    {
        _host = host;
        _questionService = questionService;
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
        var tcs = new TaskCompletionSource<IReadOnlyList<string>>();
        _host._currentQuestionPrompt = q;
        _host._currentQuestionIdx = idx;
        _host._currentQuestionTotal = total;
        _host._questionInput = "";
        _host._questionMultiSelection.Clear();
        _host._questionTcs = tcs;
        _host._questionActive = true;
        _host.InvalidateRendered();

        try
        {
            return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _host._questionActive = false;
            _host._questionTcs = null;
            _host.InvalidateRendered();
        }
    }
}
