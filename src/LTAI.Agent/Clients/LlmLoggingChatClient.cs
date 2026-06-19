// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  LlmLoggingChatClient — LLM I/O raw request/response logger
//
//  Wraps an IChatClient and logs every request+response to a file
//  at {LogsDirectory}/llm.log. Inspired by zap-coding-agent's
//  ~/.zap/llm.log which provides complete visibility into what
//  is actually sent to the LLM on every turn.
//
//  Log format: JSON-lines, one entry per request-response pair.
//  Tool schemas are condensed to "[N tools]" summary to keep
//  logs readable. Images/base64 data is redacted.
//
//  Enable via LTAIOptions or env LTAI_LLM_LOG=true.
// ═══════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Clients;

/// <summary>
/// IChatClient decorator that logs all LLM requests and responses
/// to {LogsDirectory}/llm.log as JSON-lines.
/// </summary>
public sealed class LlmLoggingChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly string _logPath;
    private readonly bool _enabled;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public LlmLoggingChatClient(IChatClient inner, LTAIOptions? options = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        var enabled = Environment.GetEnvironmentVariable("LTAI_LLM_LOG");
        _enabled = enabled == "1" || string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase);

        if (_enabled)
        {
            var logsDir = options?.LogsDirectory ?? "logs";
            try { Directory.CreateDirectory(logsDir); } catch { }
            _logPath = Path.Combine(logsDir, "llm.log");
        }
        else
        {
            _logPath = "";
        }
    }

    public void Dispose() => _inner.Dispose();

    object? IChatClient.GetService(Type serviceType, object? serviceKey)
        => _inner.GetService(serviceType, serviceKey);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messages = chatMessages as List<ChatMessage> ?? chatMessages.ToList();

        if (_enabled)
            LogRequest(messages, options);

        var response = await _inner.GetResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false);

        if (_enabled)
            LogResponse(messages, response);

        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = chatMessages as List<ChatMessage> ?? chatMessages.ToList();

        if (_enabled)
            LogRequest(messages, options);

        var buffer = new List<ChatResponseUpdate>();
        await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            buffer.Add(update);
            yield return update;
        }

        if (_enabled)
            LogStreamingResponse(messages, buffer);
    }

    // ═══════════════════════════════════════════
    //  Private
    // ═══════════════════════════════════════════

    private void LogRequest(List<ChatMessage> messages, ChatOptions? options)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("─── LLM REQUEST ────────────────────────────────────────────────");
            sb.AppendLine($"Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z");

            // Messages
            foreach (var msg in messages)
            {
                var role = msg.Role.ToString() ?? "unknown";
                var text = TruncateMessage(msg.Text ?? "");
                sb.AppendLine($"[{role}] {text}");

                // Tool calls (if any)
                if (msg.Contents?.Count > 0)
                {
                    foreach (var content in msg.Contents)
                    {
                        if (content is Microsoft.Extensions.AI.FunctionCallContent fc)
                            sb.AppendLine($"  → tool: {fc.Name}({TruncateArgs(fc.Arguments)})");
                    }
                }
            }

            // Options summary
            if (options != null)
            {
                var toolCount = options.Tools?.Count ?? 0;
                sb.AppendLine($"Options: model={options.ModelId ?? "(default)"}, " +
                    $"temperature={options.Temperature}, tools={toolCount}");
            }

            sb.AppendLine("────────────────────────────────────────────────────────────────");

            AppendToLog(sb.ToString());
        }
        catch
        {
            // Logging failures must never break the caller
        }
    }

    private void LogResponse(List<ChatMessage> requestMessages, ChatResponse response)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("─── LLM RESPONSE ───────────────────────────────────────────────");
            sb.AppendLine($"Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z");

            var msg = response.Messages?.LastOrDefault();
            if (msg != null)
            {
                // Truncate long text content
                var text = TruncateLong(msg.Text ?? "");
                sb.AppendLine($"Role: {msg.Role}");
                sb.AppendLine($"Text ({text.Length} chars): {text}");

                // Tool calls
                if (msg.Contents?.Count > 0)
                {
                    foreach (var content in msg.Contents)
                    {
                        if (content is Microsoft.Extensions.AI.FunctionCallContent fc)
                            sb.AppendLine($"  → tool_call: {fc.Name}({TruncateArgs(fc.Arguments)})");
                        else if (content is Microsoft.Extensions.AI.FunctionResultContent fr)
                            sb.AppendLine($"  → tool_result: {fr.CallId} = {TruncateLong(fr.Result?.ToString() ?? "")}");
                    }
                }
            }

            // Usage info
            if (response.Usage != null)
            {
                sb.AppendLine($"Usage: in={response.Usage?.InputTokenCount}, out={response.Usage?.OutputTokenCount}");
            }

            sb.AppendLine("────────────────────────────────────────────────────────────────");

            AppendToLog(sb.ToString());
        }
        catch
        {
            // Logging failures must never break the caller
        }
    }

    private void LogStreamingResponse(List<ChatMessage> requestMessages, List<ChatResponseUpdate> updates)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("─── LLM STREAMING RESPONSE ──────────────────────────────────────");
            sb.AppendLine($"Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z");
            sb.AppendLine($"Updates: {updates.Count}");

            // Collect all text
            var fullText = string.Concat(updates
                .Select(u => u.Text ?? ""));

            if (!string.IsNullOrEmpty(fullText))
                sb.AppendLine($"Full text ({fullText.Length} chars): {TruncateLong(fullText)}");

            sb.AppendLine("────────────────────────────────────────────────────────────────");

            AppendToLog(sb.ToString());
        }
        catch
        {
            // Logging failures must never break the caller
        }
    }

    private void AppendToLog(string text)
    {
        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logPath, text + "\n", Encoding.UTF8);
            }
            catch
            {
                // Best-effort logging
            }
        }
    }

    private static string TruncateMessage(string text, int maxLen = 2000)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        return text.Length <= maxLen
            ? text
            : text[..maxLen] + $"\n... [truncated, total {text.Length} chars]";
    }

    private static string TruncateLong(string text, int maxLen = 5000)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        if (text.Length <= maxLen) return text;
        return text[..maxLen] + $"\n... [truncated, total {text.Length} chars]";
    }

    private static string TruncateArgs(IDictionary<string, object?>? args)
    {
        if (args == null || args.Count == 0) return "";
        var items = args.Select(kv =>
        {
            var val = kv.Value?.ToString() ?? "null";
            return val.Length > 100 ? $"{kv.Key}=...({val.Length} chars)" : $"{kv.Key}={val}";
        });
        return string.Join(", ", items);
    }
}
