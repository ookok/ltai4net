using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Clients;

/// <summary>
/// IChatClient decorator that checks responses contain <c>&lt;thinking&gt;</c> tags.
/// If absent (and response is long enough to warrant reasoning), inserts a system
/// reminder for the next turn rather than blocking the output.
/// </summary>
public sealed partial class ThinkingTagValidator : IChatClient
{
    private readonly IChatClient _inner;

    [GeneratedRegex(@"<thinking>.*?</thinking>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkingTagPattern();

    public ThinkingTagValidator(IChatClient inner) => _inner = inner;

    public void Dispose() => _inner.Dispose();

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var messages = chatMessages as List<ChatMessage> ?? chatMessages.ToList();
        var response = await _inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        CheckAndRemind(response, messages);
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = chatMessages as List<ChatMessage> ?? chatMessages.ToList();
        var buffer = new System.Text.StringBuilder();
        string? lastFailedProvider = null;
        var innerStream = _inner.GetStreamingResponseAsync(messages, options, cancellationToken);
        await using (var enumerator = innerStream.GetAsyncEnumerator(cancellationToken))
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        break;
                    update = enumerator.Current;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    lastFailedProvider = "inner";
                    System.Diagnostics.Debug.WriteLine($"[ThinkingTagValidator] streaming failed: {ex.Message}");
                    break;
                }
                if (update.Contents?.OfType<TextContent>().FirstOrDefault() is { } text)
                    buffer.Append(text.Text);
                yield return update;
            }
        }
        if (lastFailedProvider == null)
        {
            var fullText = buffer.ToString();
            if (!string.IsNullOrEmpty(fullText) && fullText.Length > 100 && !ThinkingTagPattern().IsMatch(fullText))
            {
                messages.Add(new ChatMessage(ChatRole.System,
                    "[System reminder: Please enclose your reasoning in <thinking>...</thinking> tags in future responses.]"));
            }
        }
    }

    private static void CheckAndRemind(ChatResponse response, List<ChatMessage> chatMessages)
    {
        var text = response.Messages?.LastOrDefault()?.Text;
        if (string.IsNullOrEmpty(text) || text.Length < 100) return;
        if (ThinkingTagPattern().IsMatch(text)) return;

        chatMessages.Add(new ChatMessage(ChatRole.System,
            "[System reminder: Please use <thinking>...</thinking> tags to show your reasoning process.]"));
    }

    object? IChatClient.GetService(Type serviceType, object? serviceKey)
        => serviceKey is null && serviceType?.IsInstanceOfType(this) == true ? this : null;
}
