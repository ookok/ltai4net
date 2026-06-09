namespace LTAI.Core.Rendering;

public sealed record ToolCallRecord(string Name, string Args, string Result);

public readonly record struct ToolResultInfo(bool Found, bool Success, string Output, string Error);

public readonly record struct ConfirmRequest(string Title, string Message, string ExtraInfo);

public enum ConfirmChoice { Yes, Always, No, Details }

public interface IChatRenderer
{
    void OnStreamStart();
    void OnTextDelta(string delta);
    void OnToolCall(string name, string? arguments);
    void OnToolResult(string name, string result, bool success);
    void OnStreamEnd();

    void RenderMessage(string role, string content,
        IReadOnlyList<ToolCallRecord>? toolCalls = null,
        string? reasoning = null);

    void UpdateStatus(string text);
    void UpdateProgress(string frame, string text, string? elapsed);

    ToolResultInfo TryParseToolResult(string text);
    ConfirmRequest? TryParseConfirmRequest(string text);

    Task<string?> PromptUserAsync(string prompt, bool isSecret);
    Task<ConfirmChoice> ShowConfirmAsync(string title, string message, string result, string extraInfo);

    void TrimHistory();
    void AutoCompact();
    Task SaveSessionAsync();
    Task ExtractMemoryAsync(string userInput);

    void RequestRender();
    void InvalidateRender();

    string CurrentStatus { get; set; }
}
