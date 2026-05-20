using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.Browser.Interfaces;
using LTAI.Browser.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace LTAI.Browser;

public sealed class PlaywrightBrowserAgent : IBrowserAgent, IAsyncDisposable
{
    private readonly ILogger<PlaywrightBrowserAgent> _logger;
    private readonly ConcurrentDictionary<string, PwBrowserSession> _sessions = new();
    private readonly StealthBrowserAdapter? _stealthAdapter;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _initialized;

    public PlaywrightBrowserAgent(
        ILogger<PlaywrightBrowserAgent> logger,
        StealthBrowserAdapter? stealthAdapter = null)
    {
        _logger = logger;
        _stealthAdapter = stealthAdapter;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        if (_stealthAdapter != null)
        {
            _browser = await _stealthAdapter.LaunchStealthBrowserAsync();
            _logger.LogInformation("Using stealth browser adapter");
        }
        else
        {
            _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[]
                {
                    "--no-sandbox",
                    "--disable-setuid-sandbox",
                    "--disable-dev-shm-usage",
                    "--disable-blink-features=AutomationControlled"
                }
            });
        }

        _initialized = true;
    }

    public async Task<BrowserResult> BrowseAsync(string url, string task, int maxIterations = 6, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var page = await _browser!.NewPageAsync();
            await page.GotoAsync(url, new() { Timeout = 30000, WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.WaitForTimeoutAsync(2000);

            var title = await page.TitleAsync();
            var html = await page.ContentAsync();
            var items = AdaptiveExtractor.ExtractFromHtml(html, task);
            var text = ExtractText(html);

            sw.Stop();
            return new BrowserResult
            {
                Success = true, Url = url, Title = title,
                Text = text, Items = items,
                Count = items.Count, Method = "playwright",
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Playwright browse failed: {Url}", url);
            return new BrowserResult { Success = false, Error = ex.Message, ElapsedMs = sw.ElapsedMilliseconds };
        }
    }

    public async Task<ScreenshotResult> ScreenshotAsync(string? url = null, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        try
        {
            var page = _browser!.Contexts.FirstOrDefault()?.Pages.FirstOrDefault();
            if (page == null && url != null)
            {
                page = await _browser.NewPageAsync();
                await page.GotoAsync(url);
            }
            if (page == null) return new ScreenshotResult { Success = false, Error = "No active page" };

            var bytes = await page.ScreenshotAsync(new() { FullPage = true, Type = ScreenshotType.Png });
            return new ScreenshotResult
            {
                Success = true, Width = page.ViewportSize?.Width ?? 0,
                Height = page.ViewportSize?.Height ?? 0,
                Base64 = Convert.ToBase64String(bytes)
            };
        }
        catch (Exception ex) { return new ScreenshotResult { Success = false, Error = ex.Message }; }
    }

    public async Task<string> SessionOpenAsync(string? url = null, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        var context = await _browser!.NewContextAsync();
        var session = new PwBrowserSession { Id = Guid.NewGuid().ToString("N"), Context = context, CreatedAt = DateTime.UtcNow };

        if (url != null)
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync(url);
            session.Page = page;
        }

        _sessions[session.Id] = session;
        return session.Id;
    }

    public async Task SessionCloseAsync(string sessionId, CancellationToken ct = default)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            await session.Context.CloseAsync();
        }
    }

    public async Task<List<BrowserSession>> SessionListAsync(CancellationToken ct = default)
    {
        var list = new List<BrowserSession>();
        foreach (var (id, s) in _sessions)
        {
            list.Add(new BrowserSession { SessionId = id, CurrentUrl = s.Page?.Url, CreatedAt = s.CreatedAt });
        }
        return list;
    }

    public async Task<PageState> ExtractPageStateAsync(object? context = null, CancellationToken ct = default)
    {
        if (context is not IPage page)
        {
            page = _browser?.Contexts.FirstOrDefault()?.Pages.FirstOrDefault();
            if (page == null) return new PageState();
        }

        try
        {
            var text = await page.EvaluateAsync<string>(@"() => {
                const b = document.body;
                if (!b) return '';
                const c = b.cloneNode(true);
                c.querySelectorAll('script, style, noscript, iframe, svg').forEach(s => s.remove());
                return (c.textContent || '').substring(0, 50000);
            }");

            return new PageState { Text = text ?? "", Items = new List<PageItem>() };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Page state extraction partial");
            return new PageState();
        }
    }

    public async Task<BrowserResult> ClickAsync(string selector, CancellationToken ct = default)
    {
        var page = GetActivePage();
        if (page == null) return new BrowserResult { Success = false, Error = "No active page" };

        try { await page.ClickAsync(selector); await page.WaitForTimeoutAsync(1000); return new BrowserResult { Success = true }; }
        catch (Exception ex) { return new BrowserResult { Success = false, Error = ex.Message }; }
    }

    public async Task<BrowserResult> FillAsync(string selector, string text, CancellationToken ct = default)
    {
        var page = GetActivePage();
        if (page == null) return new BrowserResult { Success = false, Error = "No active page" };
        try { await page.FillAsync(selector, text); return new BrowserResult { Success = true }; }
        catch (Exception ex) { return new BrowserResult { Success = false, Error = ex.Message }; }
    }

    public async Task<string> EvaluateAsync(string script, CancellationToken ct = default)
    {
        var page = GetActivePage();
        if (page == null) return "No active page";
        try { var r = await page.EvaluateAsync<object>(script); return r?.ToString() ?? "undefined"; }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    private IPage? GetActivePage() =>
        _sessions.Values.FirstOrDefault(s => s.Page != null)?.Page
        ?? _browser?.Contexts.FirstOrDefault()?.Pages.FirstOrDefault();

    private static string ExtractText(string html)
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);
        var body = doc.DocumentNode.SelectSingleNode("//body");
        return body?.InnerText?[..Math.Min(body.InnerText?.Length ?? 0, 50000)] ?? "";
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, s) in _sessions) await s.Context.CloseAsync();
        _sessions.Clear();
        if (_browser != null) await _browser.CloseAsync();
        _playwright?.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal sealed class PwBrowserSession
{
    public string Id { get; init; } = "";
    public IBrowserContext Context { get; init; } = null!;
    public IPage? Page { get; set; }
    public DateTime CreatedAt { get; init; }
}
