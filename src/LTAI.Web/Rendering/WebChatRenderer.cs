using System.Text.Json;
using LTAI.Core.Rendering;
using Microsoft.AspNetCore.Mvc;

namespace LTAI.Web.Rendering;

public sealed class WebChatRenderer : IChatRenderer
{
    private readonly HttpResponse _response;
    private string _status = "";

    public WebChatRenderer(HttpResponse response)
    {
        _response = response;
    }

    public void OnStreamStart() { }
    public void OnStreamEnd() { }

    public void OnTextDelta(string delta)
    {
        var payload = JsonSerializer.Serialize(new { type = "delta", text = delta });
        WriteSse(payload);
    }

    public void OnToolCall(string name, string? arguments)
    {
        var payload = JsonSerializer.Serialize(new { type = "tool_call", name, arguments });
        WriteSse(payload);
    }

    public void OnToolResult(string name, string result, bool success)
    {
        var payload = JsonSerializer.Serialize(new { type = "tool_result", name, result, success });
        WriteSse(payload);
    }

    public void RenderMessage(string role, string content,
        IReadOnlyList<ToolCallRecord>? toolCalls, string? reasoning)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = "message",
            role,
            content,
            toolCalls = toolCalls?.Select(t => new { t.Name, t.Args, t.Result }),
            reasoning
        });
        WriteSse(payload);
    }

    public void UpdateStatus(string text) => _status = text;

    public void UpdateProgress(string frame, string text, string? elapsed)
    {
        var payload = JsonSerializer.Serialize(new { type = "progress", frame, text, elapsed });
        WriteSse(payload);
    }

    public ToolResultInfo TryParseToolResult(string text) => default;
    public ConfirmRequest? TryParseConfirmRequest(string text) => null;

    public Task<string?> PromptUserAsync(string prompt, bool isSecret)
        => Task.FromResult<string?>(null);

    public Task<ConfirmChoice> ShowConfirmAsync(string title, string message, string result, string extraInfo)
        => Task.FromResult(ConfirmChoice.Yes);

    public void TrimHistory() { }
    public void AutoCompact() { }
    public Task SaveSessionAsync() => Task.CompletedTask;
    public Task ExtractMemoryAsync(string userInput) => Task.CompletedTask;

    public void RequestRender() { }
    public void InvalidateRender() { }

    public string CurrentStatus
    {
        get => _status;
        set => _status = value;
    }

    public async Task WriteDoneAsync()
    {
        await _response.WriteAsync("data: [DONE]\n\n");
        await _response.Body.FlushAsync();
    }

    public async Task WriteErrorAsync(string error)
    {
        var payload = JsonSerializer.Serialize(new { type = "error", error });
        await _response.WriteAsync($"data: {payload}\n\n");
        await _response.Body.FlushAsync();
    }

    private void WriteSse(string payload)
    {
        _response.WriteAsync($"data: {payload}\n\n").ConfigureAwait(false).GetAwaiter().GetResult();
        _response.Body.FlushAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }
}
