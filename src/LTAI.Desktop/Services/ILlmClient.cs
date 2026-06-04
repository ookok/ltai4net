namespace LTAI.Desktop.Services;

public interface ILlmClient
{
    Task<string> ChatAsync(string message, CancellationToken ct = default);

    IAsyncEnumerable<string> ChatStreamingAsync(string message, CancellationToken ct = default);
}
