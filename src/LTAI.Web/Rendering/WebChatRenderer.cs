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

    public async Task OnTextDelta(string delta)
    {
        var payload = JsonSerializer.Serialize(new { type = "delta", text = delta });
        await WriteSseAsync(payload).ConfigureAwait(false);
    }

    void IChatRenderer.OnTextDelta(string delta) => _ = OnTextDelta(delta);

    public async Task OnToolCall(string name, string? arguments)
    {
        var payload = JsonSerializer.Serialize(new { type = "tool_call", name, arguments });
        await WriteSseAsync(payload).ConfigureAwait(false);
    }

    void IChatRenderer.OnToolCall(string name, string? arguments) => _ = OnToolCall(name, arguments);

    public async Task OnToolResult(string name, string result, bool success)
    {
        var payload = JsonSerializer.Serialize(new { type = "tool_result", name, result, success });
        await WriteSseAsync(payload).ConfigureAwait(false);
    }

    void IChatRenderer.OnToolResult(string name, string result, bool success) => _ = OnToolResult(name, result, success);

    public async Task RenderMessage(string role, string content,
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
        await WriteSseAsync(payload).ConfigureAwait(false);
    }

    void IChatRenderer.RenderMessage(string role, string content,
        IReadOnlyList<ToolCallRecord>? toolCalls, string? reasoning)
            => _ = RenderMessage(role, content, toolCalls, reasoning);

    public void UpdateStatus(string text) => _status = text;

    public async Task UpdateProgress(string frame, string text, string? elapsed)
    {
        var payload = JsonSerializer.Serialize(new { type = "progress", frame, text, elapsed });
        await WriteSseAsync(payload).ConfigureAwait(false);
    }

    void IChatRenderer.UpdateProgress(string frame, string text, string? elapsed)
        => _ = UpdateProgress(frame, text, elapsed);

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

    private async ValueTask WriteSseAsync(string payload)
    {
        await _response.WriteAsync($"data: {payload}\n\n").ConfigureAwait(false);
        await _response.Body.FlushAsync().ConfigureAwait(false);
    }
}
