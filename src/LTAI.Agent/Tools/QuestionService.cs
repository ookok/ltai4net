using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

/// <summary>
/// Bridges LLM tool calls and UI. When the LLM calls <c>ask_questions</c>,
/// this service fires <see cref="QuestionPosted"/> for the TUI/Desktop to
/// render and collect answers. The tool call awaits the user's reply via a
/// <see cref="TaskCompletionSource{T}"/>.
/// </summary>
public sealed class QuestionService
{
    private readonly ConcurrentDictionary<Guid, PendingEntry> _pending = new();
    private readonly ILogger<QuestionService> _logger;

    public QuestionService(ILogger<QuestionService> logger)
    {
        _logger = logger;
    }

    /// <summary>Fired when the LLM has questions for the user. Subscribe in TUI/Desktop.</summary>
    public event Action<QuestionPost>? QuestionPosted;

    /// <summary>
    /// Called by the AITool. Submits questions and waits for the user to
    /// answer via <see cref="Reply"/> or <see cref="Reject"/>.
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyList<string>>> AskAsync(
        IReadOnlyList<QuestionPrompt> questions,
        CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var tcs = new TaskCompletionSource<IReadOnlyList<IReadOnlyList<string>>>();
        var post = new QuestionPost(id, questions);
        _pending[id] = new PendingEntry(post, tcs);
        _logger.LogDebug("Question posted: {Id} ({Count} questions)", id, questions.Count);

        try
        {
            QuestionPosted?.Invoke(post);
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Called by UI after the user answers.</summary>
    public void Reply(Guid requestId, IReadOnlyList<IReadOnlyList<string>> answers)
    {
        if (_pending.TryGetValue(requestId, out var entry))
        {
            _logger.LogDebug("Question replied: {Id}", requestId);
            entry.Tcs.TrySetResult(answers);
        }
    }

    /// <summary>Called by UI when the user dismisses.</summary>
    public void Reject(Guid requestId)
    {
        if (_pending.TryGetValue(requestId, out var entry))
        {
            _logger.LogDebug("Question rejected: {Id}", requestId);
            entry.Tcs.TrySetCanceled();
        }
    }

    private sealed record PendingEntry(QuestionPost Post, TaskCompletionSource<IReadOnlyList<IReadOnlyList<string>>> Tcs);
}

/// <summary>A single question the LLM wants to ask.</summary>
public sealed record QuestionPrompt(
    string Question,
    string Header,
    IReadOnlyList<QuestionOption> Options,
    bool Multiple = false);

/// <summary>One choice within a question.</summary>
public sealed record QuestionOption(string Label, string Description);

/// <summary>Payload for UI subscribers.</summary>
public sealed record QuestionPost(Guid RequestId, IReadOnlyList<QuestionPrompt> Questions);
