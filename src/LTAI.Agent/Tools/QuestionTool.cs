using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Tools;

/// <summary>
/// P17.5: LLM tool for asking structured follow-up questions. Mirrors
/// opencode's <c>question</c> tool — the LLM sends a batch of questions
/// with options, the UI renders them, and answers are returned inline.
/// </summary>
public sealed class QuestionTool
{
    private readonly QuestionService _service;

    public QuestionTool(QuestionService service)
    {
        _service = service;
    }

    /// <summary>
    /// Ask the user one or more questions when the task is ambiguous or you
    /// need clarification. Each question can offer multiple choices; the user
    /// may also type a free-form answer. Answers are returned as arrays of
    /// selected labels (one array per question, in order).
    /// </summary>
    [Description("当需求不清晰时向用户提出结构化问题以澄清要求。支持多选和自定义答案。")]
    public async Task<string> AskQuestions(
        [Description("问题列表，每个问题含标题、简短标头、选项和是否允许多选")]
        IReadOnlyList<QuestionPrompt> questions,
        CancellationToken ct = default)
    {
        if (questions == null || questions.Count == 0)
            return "No questions provided.";

        var answers = await _service.AskAsync(questions, ct).ConfigureAwait(false);

        var parts = new List<string>(questions.Count);
        for (int i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            var a = i < answers.Count ? answers[i] : (IReadOnlyList<string>)Array.Empty<string>();
            parts.Add($"\"{q.Header}\"=\"{string.Join(", ", a)}\"");
        }

        return $"User has answered your questions: {string.Join("; ", parts)}. Continue with the answers in mind.";
    }
}
