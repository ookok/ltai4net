using LTAI.Infra.Browser.Models;

namespace LTAI.Infra.Browser.Interfaces;

public interface IBrowserAgent
{
    Task<BrowserResult> BrowseAsync(string url, string task, int maxIterations = 6, CancellationToken cancellationToken = default);
    Task<ScreenshotResult> ScreenshotAsync(string? url = null, CancellationToken cancellationToken = default);
    Task<string> SessionOpenAsync(string? url = null, CancellationToken cancellationToken = default);
    Task SessionCloseAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<List<BrowserSession>> SessionListAsync(CancellationToken cancellationToken = default);
    Task<PageState> ExtractPageStateAsync(object? context = null, CancellationToken cancellationToken = default);
    Task<BrowserResult> ClickAsync(string selector, CancellationToken cancellationToken = default);
    Task<BrowserResult> FillAsync(string selector, string text, CancellationToken cancellationToken = default);
    Task<string> EvaluateAsync(string script, CancellationToken cancellationToken = default);
}
