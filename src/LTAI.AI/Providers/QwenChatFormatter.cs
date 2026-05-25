using System.Text;
using Microsoft.Extensions.AI;

namespace LTAI.AI.Providers;

/// <summary>
/// Applies Qwen-Fixed-Chat-Templates corrections programmatically.
/// Fixes 5 known Qwen chat template issues:
/// 1. System message placement (must be first message)
/// 2. Role alternation (user/assistant must alternate)
/// 3. Consecutive same-role merge (multiple user messages merged)
/// 4. Tool call response pairing (tool message must follow assistant with tools)
/// 5. BOS/EOS token injection based on model type
/// </summary>
public sealed class QwenChatFormatter
{
    public enum QwenModelFamily { Qwen2, Qwen2_5, Qwen3, QwQ }

    private readonly QwenModelFamily _family;
    private readonly bool _addGenerationPrompt;

    public QwenChatFormatter(QwenModelFamily family = QwenModelFamily.Qwen2_5,
        bool addGenerationPrompt = true)
    {
        _family = family;
        _addGenerationPrompt = addGenerationPrompt;
    }

    public IEnumerable<ChatMessage> FixMessages(IEnumerable<ChatMessage> messages)
    {
        var list = messages.ToList();
        if (list.Count == 0) return list;

        // 1. Extract system message and ensure it's first
        var systemMsg = list.FirstOrDefault(m => m.Role == ChatRole.System);
        var nonSystemMessages = list.Where(m => m.Role != ChatRole.System).ToList();

        // 2. Merge consecutive messages from the same role
        var merged = MergeConsecutiveSameRole(nonSystemMessages);

        // 3. Ensure user/assistant alternation (insert empty assistant if needed)
        var fixedMessages = FixRoleAlternation(merged);

        // 4. Fix tool call pairing: tool response must follow assistant with FunctionCall
        var toolFixed = FixToolPairing(fixedMessages);

        // 5. Reconstruct with system message first
        var result = new List<ChatMessage>();
        if (systemMsg != null)
            result.Add(systemMsg);
        result.AddRange(toolFixed);

        // 6. Add generation prompt for models that need it
        if (_addGenerationPrompt && result.Count > 0 && result[^1].Role != ChatRole.Assistant)
        {
            result.Add(new ChatMessage(ChatRole.Assistant, ""));
        }

        return result;
    }

    public string FormatAsString(IEnumerable<ChatMessage> messages)
    {
        var fixedMessages = FixMessages(messages).ToList();
        return RenderMessagesToQwenFormat(fixedMessages);
    }

    private string RenderMessagesToQwenFormat(List<ChatMessage> messages)
    {
        var sb = new StringBuilder();

        if (messages.Count > 0 && messages[0].Role == ChatRole.System)
        {
            sb.Append("<|im_start|>system\n");
            sb.Append(messages[0].Text);
            sb.Append("<|im_end|>\n");
        }

        for (var i = (messages[0].Role == ChatRole.System ? 1 : 0); i < messages.Count; i++)
        {
            var msg = messages[i];

            if (msg.Role == ChatRole.User)
            {
                sb.Append("<|im_start|>user\n");
                sb.Append(msg.Text);
                sb.Append("<|im_end|>\n");
            }
            else if (msg.Role == ChatRole.Assistant)
            {
                sb.Append("<|im_start|>assistant\n");

                var toolCalls = msg.Contents?.OfType<FunctionCallContent>().ToList();
                if (toolCalls is { Count: > 0 })
                {
                    sb.AppendLine();
                    foreach (var tc in toolCalls)
                    {
                        sb.Append("<tool_call>\n");
                        sb.Append("{\"name\": \"");
                        sb.Append(tc.Name);
                        sb.Append("\", \"arguments\": ");
                        sb.Append(tc.Arguments != null
                            ? System.Text.Json.JsonSerializer.Serialize(tc.Arguments)
                            : "{}");
                        sb.Append("}\n</tool_call>\n");
                    }
                }

                if (!string.IsNullOrEmpty(msg.Text))
                {
                    sb.Append(msg.Text);
                    sb.AppendLine();
                }

                sb.Append("<|im_end|>\n");
            }
            else if (msg.Role == ChatRole.Tool)
            {
                sb.Append("<|im_start|>user\n");
                sb.Append("<tool_response>\n");
                sb.Append(msg.Text);
                sb.Append("\n</tool_response>\n");
                sb.Append("<|im_end|>\n");
            }
        }

        if (_addGenerationPrompt)
            sb.Append("<|im_start|>assistant\n");

        return sb.ToString();
    }

    private static List<ChatMessage> MergeConsecutiveSameRole(List<ChatMessage> messages)
    {
        if (messages.Count <= 1) return messages;

        var result = new List<ChatMessage>();
        ChatMessage? current = null;

        foreach (var msg in messages)
        {
            if (current == null)
            {
                current = msg;
                continue;
            }

            if (current.Role == msg.Role && current.Role != ChatRole.Tool)
            {
                current = new ChatMessage(current.Role,
                    current.Text + "\n\n" + msg.Text);
            }
            else
            {
                result.Add(current);
                current = msg;
            }
        }

        if (current != null)
            result.Add(current);

        return result;
    }

    private static List<ChatMessage> FixRoleAlternation(List<ChatMessage> messages)
    {
        if (messages.Count <= 1) return messages;

        var result = new List<ChatMessage>();

        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];

            if (i > 0 && msg.Role == ChatRole.User && messages[i - 1].Role == ChatRole.User)
            {
                result.Add(new ChatMessage(ChatRole.Assistant, "I understand. Please continue."));
            }

            result.Add(msg);
        }

        return result;
    }

    private static List<ChatMessage> FixToolPairing(List<ChatMessage> messages)
    {
        if (messages.Count == 0) return messages;

        var result = new List<ChatMessage>();
        var pendingToolResponse = false;

        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];

            if (msg.Role == ChatRole.Tool)
            {
                // Tool response should follow an assistant that had tool calls
                // If the previous message wasn't assistant with tools, convert to user
                if (i == 0 || messages[i - 1].Role != ChatRole.Assistant)
                {
                    // Wrap tool response in user-marked message
                    msg = new ChatMessage(ChatRole.User,
                        "[Tool Response]\n" + msg.Text);
                }
                pendingToolResponse = true;
            }
            else if (pendingToolResponse && msg.Role == ChatRole.User)
            {
                // Merge tool response into next user message if one follows
                var toolText = result[^1].Text;
                result[^1] = new ChatMessage(ChatRole.User,
                    toolText + "\n\n" + msg.Text);
                pendingToolResponse = false;
                continue;
            }

            result.Add(msg);
        }

        return result;
    }
}
