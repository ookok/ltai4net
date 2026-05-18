using LTAI.Browser.Models;

namespace LTAI.Browser.Interfaces;

public interface IBrowserAgent
{
    Task<BrowserResult> BrowseAsync(string url, string task, int maxIterations = 6, CancellationToken cancellationToken = default);

    Task<ScreenshotResult> ScreenshotAsync(CancellationToken cancellationToken = default);

    Task<string> SessionOpenAsync(string? url = null, CancellationToken cancellationToken = default);

    Task SessionCloseAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BrowserSession>> SessionListAsync(CancellationToken cancellationToken = default);

    Task<PageState> ExtractPageStateAsync(CancellationToken cancellationToken = default);
}
