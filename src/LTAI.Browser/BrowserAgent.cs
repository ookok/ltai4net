using System.Diagnostics;
using System.Text.Json;
using LTAI.Browser.Interfaces;
using LTAI.Browser.Models;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;

namespace LTAI.Browser;

public sealed class BrowserAgent : IBrowserAgent, IAsyncDisposable
{
    private readonly ILogger<BrowserAgent> _logger;
    private readonly Dictionary<string, SessionState> _sessions = new();
    private string? _activeSessionId;
    private static readonly string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private sealed class SessionState : IDisposable
    {
        public IBrowser Browser { get; init; } = null!;
        public IPage Page { get; init; } = null!;
        public DateTime CreatedAt { get; init; }
        public int PageViews { get; set; }

        public void Dispose()
        {
            try { Page?.Dispose(); } catch { }
            try { Browser?.Dispose(); } catch { }
        }
    }

    public BrowserAgent(ILogger<BrowserAgent> logger)
    {
        _logger = logger;
    }

    public async Task<BrowserResult> BrowseAsync(
        string url,
        string task,
        int maxIterations = 6,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            using var sessionLease = await CreateBrowserSessionAsync();
            var page = sessionLease.Page;

            await NavigateAsync(page, url);

            List<Dictionary<string, object?>>? items = null;

            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var state = await ExtractPageStateInternalAsync(page);
                var textSample = state.Text.Length > 3000 ? state.Text[..3000] : state.Text;

                if (string.IsNullOrWhiteSpace(textSample) ||
                    textSample.Length < 50)
                {
                    items = await AdaptiveExtractor.ExtractAsync(page, task);
                    if (items is { Count: > 0 })
                        break;
                }
                else
                {
                    items = await AdaptiveExtractor.ExtractAsync(page, task);
                    if (items is { Count: > 0 })
                        break;
                }

                await Task.Delay(500, cancellationToken);
            }

            items ??= await AdaptiveExtractor.ExtractAsync(page, task);

            var title = await page.GetTitleAsync();
            sw.Stop();

            _logger.LogInformation("Browse completed: {Url}, items: {Count}, elapsed: {Elapsed}ms",
                url, items.Count, sw.ElapsedMilliseconds);

            return BrowserResult.Ok(url, title, items, "puppeteer", sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogError(ex, "Browse failed: {Url}", url);
            return BrowserResult.Fail(url, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public async Task<ScreenshotResult> ScreenshotAsync(CancellationToken cancellationToken = default)
    {
        var state = GetActiveSession();
        if (state == null)
            return new ScreenshotResult { Success = false, Error = "No active browser session" };

        try
        {
            var data = await state.Page.ScreenshotDataAsync(new ScreenshotOptions
            {
                FullPage = false,
                Type = ScreenshotType.Png
            });

            var viewport = state.Page.Viewport;
            return new ScreenshotResult
            {
                Success = true,
                Base64 = Convert.ToBase64String(data),
                Width = (int)(viewport?.Width ?? 1920),
                Height = (int)(viewport?.Height ?? 1080)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Screenshot failed");
            return new ScreenshotResult { Success = false, Error = ex.Message };
        }
    }

    public async Task<string> SessionOpenAsync(string? url = null, CancellationToken cancellationToken = default)
    {
        var sessionId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var session = await CreateBrowserSessionAsync();

        var page = session.Page;

        if (!string.IsNullOrEmpty(url))
        {
            await NavigateAsync(page, url);
        }

        _sessions[sessionId] = session;
        _activeSessionId = sessionId;

        _logger.LogInformation("Session opened: {SessionId}, url: {Url}", sessionId, url);
        return sessionId;
    }

    public Task SessionCloseAsync(CancellationToken cancellationToken = default)
    {
        if (_activeSessionId == null || !_sessions.TryGetValue(_activeSessionId, out var state))
            return Task.CompletedTask;

        return CloseSessionInternalAsync(_activeSessionId, state);
    }

    public Task<IReadOnlyList<BrowserSession>> SessionListAsync(CancellationToken cancellationToken = default)
    {
        var list = _sessions
            .Select(kvp =>
            {
                var state = kvp.Value;
                return new BrowserSession
                {
                    SessionId = kvp.Key,
                    CurrentUrl = state.Page.Url,
                    CreatedAt = state.CreatedAt,
                    PageViews = state.PageViews
                };
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<BrowserSession>>(list);
    }

    public Task<PageState> ExtractPageStateAsync(CancellationToken cancellationToken = default)
    {
        var state = GetActiveSession();
        if (state == null)
            return Task.FromResult(new PageState());

        return ExtractPageStateInternalAsync(state.Page);
    }

    private async Task<SessionState> CreateBrowserSessionAsync()
    {
        var browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            Args = new[]
            {
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-blink-features=AutomationControlled",
                "--disable-dev-shm-usage",
                "--disable-web-security"
            }
        });

        var page = await browser.NewPageAsync();
        await page.SetUserAgentAsync(DefaultUserAgent);
        await page.SetViewportAsync(new ViewPortOptions
        {
            Width = 1920,
            Height = 1080
        });

        return new SessionState
        {
            Browser = browser,
            Page = page,
            CreatedAt = DateTime.UtcNow
        };
    }

    private async Task NavigateAsync(IPage page, string url)
    {
        await page.GoToAsync(url, new NavigationOptions
        {
            Timeout = 30000,
            WaitUntil = new[] { WaitUntilNavigation.Networkidle2 }
        });
    }

    private SessionState? GetActiveSession()
    {
        if (_activeSessionId != null && _sessions.TryGetValue(_activeSessionId, out var state))
            return state;
        return null;
    }

    private async Task CloseSessionInternalAsync(string sessionId, SessionState state)
    {
        try
        {
            await state.Page.CloseAsync();
            await state.Browser.CloseAsync();
            _sessions.Remove(sessionId);
            if (_activeSessionId == sessionId)
            {
                _activeSessionId = _sessions.Keys.FirstOrDefault();
            }
            _logger.LogInformation("Session closed: {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to close session: {SessionId}", sessionId);
        }
    }

    internal static async Task<PageState> ExtractPageStateInternalAsync(IPage page)
    {
        var state = new PageState();

        try
        {
            var jsInputs = @"
                (() => {
                    const inputs = [];
                    const els = document.querySelectorAll('input:not([type=""hidden""]), textarea, select');
                    for (let i = 0; i < Math.min(els.length, 30); i++) {
                        const el = els[i];
                        let sel = el.id ? '#' + CSS.escape(el.id) :
                            el.className ? '.' + el.className.split(' ')[0] :
                            el.name ? '[name=""' + el.name + '""]' : '';
                        inputs.push({
                            selector: sel || el.tagName.toLowerCase(),
                            type: (el.getAttribute('type') || el.tagName.toLowerCase()),
                            placeholder: (el.getAttribute('placeholder') || '').substring(0, 60),
                            visible: !!el.offsetParent
                        });
                    }
                    return inputs;
                })()";

            var inputs = await page.EvaluateFunctionAsync<List<PageInput>>(jsInputs);
            state.Inputs = inputs ?? new List<PageInput>();

            var jsClickables = @"
                (() => {
                    const clickables = [];
                    const els = document.querySelectorAll('button, a, [role=""button""], [onclick]');
                    for (let i = 0; i < Math.min(els.length, 40); i++) {
                        const el = els[i];
                        const text = (el.textContent || '').trim().substring(0, 40);
                        if (!text && !el.id) continue;
                        let sel = el.id ? '#' + CSS.escape(el.id) :
                            el.className ? '.' + el.className.split(' ')[0] : el.tagName.toLowerCase();
                        if (el.offsetParent || text) {
                            clickables.push({
                                selector: sel,
                                text: text,
                                visible: !!el.offsetParent
                            });
                        }
                    }
                    return clickables;
                })()";

            var clickables = await page.EvaluateFunctionAsync<List<PageClickable>>(jsClickables);
            state.Clickables = clickables ?? new List<PageClickable>();

            var jsText = @"
                (() => {
                    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
                    let text = '';
                    let node;
                    while ((node = walker.nextNode())) {
                        if (['SCRIPT','STYLE','NOSCRIPT','SVG'].includes(node.parentElement?.tagName||'')) continue;
                        const t = node.textContent.trim();
                        if (t.length > 1) text += t + ' ';
                        if (text.length > 5000) break;
                    }
                    return text;
                })()";

            var text = await page.EvaluateFunctionAsync<string>(jsText) ?? string.Empty;
            state.Text = text;
        }
        catch (Exception)
        {
            state.Text = string.Empty;
        }

        return state;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (id, state) in _sessions.ToList())
        {
            await CloseSessionInternalAsync(id, state);
        }
        _sessions.Clear();
        GC.SuppressFinalize(this);
    }
}
