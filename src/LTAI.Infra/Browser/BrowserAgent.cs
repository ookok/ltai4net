using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.Infra.Browser.Interfaces;
using LTAI.Infra.Browser.Models;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace LTAI.Infra.Browser;

public sealed class PlaywrightBrowserAgent : IBrowserAgent, IAsyncDisposable
{
    private readonly ILogger<PlaywrightBrowserAgent> _logger;
    private readonly ConcurrentDictionary<string, PwBrowserSession> _sessions = new();
    private readonly StealthBrowserAdapter? _stealthAdapter;
    private readonly StealthBrowserConfig _config;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _initialized;

    // 常见追踪器域名黑名单 (部分示例)
    private static readonly HashSet<string> TrackerDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "google-analytics.com", "analytics.google.com", "facebook.com", "connect.facebook.net",
        "doubleclick.net", "adservice.google.com", "adsystem.com", "amazon-adsystem.com",
        "hotjar.com", "hotjar.io", "mixpanel.com", "segment.io", "optimizely.com",
        "cloudflareinsights.com", "datadoghq.com", "sentry.io", "newrelic.com"
    };

    public PlaywrightBrowserAgent(
        ILogger<PlaywrightBrowserAgent> logger,
        StealthBrowserAdapter? stealthAdapter = null,
        StealthBrowserConfig? config = null)
    {
        _logger = logger;
        _stealthAdapter = stealthAdapter;
        _config = config ?? new StealthBrowserConfig();
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
            
            var launchOptions = new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[]
                {
                    "--no-sandbox",
                    "--disable-setuid-sandbox",
                    "--disable-dev-shm-usage",
                    "--disable-blink-features=AutomationControlled",
                    "--disable-extensions",
                    "--disable-default-apps",
                    "--disable-sync",
                    "--disable-translate",
                    "--metrics-recording-only",
                    "--no-first-run",
                    "--safebrowsing-disable-auto-update",
                    "--remote-debugging-port=0" // 随机端口
                }
            };

            // 如果配置了代理，可以在启动时或 Context 时设置
            // Playwright 推荐在 Context 级别设置代理以便隔离
            
            _browser = await _playwright.Chromium.LaunchAsync(launchOptions);
        }

        _initialized = true;
    }

    /// <summary>
    /// 创建具有反检测特性的 BrowserContext
    /// </summary>
    private async Task<IBrowserContext> CreateStealthContextAsync()
    {
        await EnsureInitializedAsync();

        var contextOptions = new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            JavaScriptEnabled = true,
            Locale = "zh-CN",
            TimezoneId = "Asia/Shanghai",
            UserAgent = _config.UserAgent,
            ViewportSize = _config.RandomizeViewport 
                ? new ViewportSize { Width = 1920 + Random.Shared.Next(-200, 200), Height = 1080 + Random.Shared.Next(-100, 100) } 
                : new ViewportSize { Width = 1920, Height = 1080 },
            ScreenSize = new ScreenSize { Width = 1920, Height = 1080 },
            ColorScheme = ColorScheme.Light
        };

        if (!string.IsNullOrEmpty(_config.Proxy))
        {
            contextOptions.Proxy = new Proxy
            {
                Server = _config.Proxy
            };
        }

        var context = await _browser!.NewContextAsync(contextOptions);

        // 1. 注入 Stealth 脚本 (CDP 层模拟 addScriptToEvaluateOnNewDocument)
        if (_config.InjectStealthScripts)
        {
            await context.AddInitScriptAsync(StealthScripts.CoreStealth);
            await context.AddInitScriptAsync(StealthScripts.RandomizeFingerprint);
        }

        // 2. 追踪器拦截
        if (_config.BlockTrackers)
        {
            await context.RouteAsync("**/*", async route =>
            {
                var url = route.Request.Url;
                var isTracker = TrackerDomains.Any(domain => url.Contains(domain));
                
                if (isTracker)
                {
                    await route.AbortAsync();
                }
                else
                {
                    await route.ContinueAsync();
                }
            });
        }

        return context;
    }

    public async Task<BrowserResult> BrowseAsync(string url, string task, int maxIterations = 6, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IBrowserContext? context = null;

        try
        {
            context = await CreateStealthContextAsync();
            var page = await context.NewPageAsync();
            
            // 优化等待策略：使用 NetworkIdle 确保动态内容加载
            // 对应方案中的 --wait-until networkidle0
            await page.GotoAsync(url, new() 
            { 
                Timeout = 45000, 
                WaitUntil = WaitUntilState.NetworkIdle 
            });

            // 额外的人类行为模拟：随机滚动
            await SimulateHumanBehaviorAsync(page);

            var title = await page.TitleAsync();
            var html = await page.ContentAsync();
            var items = AdaptiveExtractor.ExtractFromHtml(html, task);
            var text = ExtractText(html);

            sw.Stop();
            return new BrowserResult
            {
                Success = true, Url = url, Title = title,
                Text = text, Items = items,
                Count = items.Count, Method = "playwright_stealth",
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Playwright stealth browse failed: {Url}", url);
            return new BrowserResult { Success = false, Error = ex.Message, ElapsedMs = sw.ElapsedMilliseconds };
        }
        finally
        {
            if (context != null) await context.CloseAsync();
        }
    }

    /// <summary>
    /// 模拟真实用户行为 (微动鼠标、随机滚动)
    /// </summary>
    private static async Task SimulateHumanBehaviorAsync(IPage page)
    {
        try
        {
            // 随机微滚动
            var scrollAmount = Random.Shared.Next(100, 500);
            await page.EvaluateAsync($"window.scrollBy(0, {scrollAmount})");
            await page.WaitForTimeoutAsync(Random.Shared.Next(500, 1500));
            
            // 随机微移鼠标 (模拟)
            var x = Random.Shared.Next(100, 800);
            var y = Random.Shared.Next(100, 600);
            await page.Mouse.MoveAsync(x, y);
        }
        catch { /* Ignore simulation errors */ }
    }

    public async Task<ScreenshotResult> ScreenshotAsync(string? url = null, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        IBrowserContext? context = null;
        try
        {
            context = await CreateStealthContextAsync();
            var page = await context.NewPageAsync();
            
            if (url != null)
            {
                await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.NetworkIdle });
            }
            
            var bytes = await page.ScreenshotAsync(new() { FullPage = true, Type = ScreenshotType.Png });
            return new ScreenshotResult
            {
                Success = true, Width = page.ViewportSize?.Width ?? 0,
                Height = page.ViewportSize?.Height ?? 0,
                Base64 = Convert.ToBase64String(bytes)
            };
        }
        catch (Exception ex) { return new ScreenshotResult { Success = false, Error = ex.Message }; }
        finally
        {
            if (context != null) await context.CloseAsync();
        }
    }

    public async Task<string> SessionOpenAsync(string? url = null, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        var context = await CreateStealthContextAsync();
        var session = new PwBrowserSession { Id = Guid.NewGuid().ToString("N"), Context = context, CreatedAt = DateTime.UtcNow };

        if (url != null)
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.NetworkIdle });
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
        IPage? page = null;
        if (context is IPage ctxPage)
        {
            page = ctxPage;
        }
        else
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

/// <summary>
/// Stealth 脚本集合
/// </summary>
internal static class StealthScripts
{
    /// <summary>
    /// 核心反检测脚本：隐藏 webdriver 属性，修补插件列表等
    /// </summary>
    public const string CoreStealth = @"
        // 隐藏 webdriver 属性
        Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
        
        // 伪造插件列表 (模拟真实 Chrome)
        Object.defineProperty(navigator, 'plugins', {
            get: () => [
                { name: 'Chrome PDF Plugin', filename: 'internal-pdf-viewer' },
                { name: 'Chrome PDF Viewer', filename: 'mhjfbmdgcfjbbpaeojofohoefgiehjai' },
                { name: 'Native Client', filename: 'internal-nacl-plugin' }
            ]
        });
        
        // 伪造语言
        Object.defineProperty(navigator, 'languages', { get: () => ['zh-CN', 'zh', 'en'] });
        
        // 隐藏 iframe 属性
        delete document.body.__proto__.chrome;
        
        // 伪装 permissions
        const originalQuery = window.navigator.permissions.query;
        window.navigator.permissions.query = (parameters) => (
            parameters.name === 'notifications' ?
                Promise.resolve({ state: Notification.permission }) :
                originalQuery(parameters)
        );
    ";

    /// <summary>
    /// 随机化指纹脚本 (WebGL, AudioContext 等)
    /// </summary>
    public const string RandomizeFingerprint = @"
        // WebGL 指纹随机化 (添加微小噪声)
        const getParameter = WebGLRenderingContext.prototype.getParameter;
        WebGLRenderingContext.prototype.getParameter = function(parameter) {
            const result = getParameter.call(this, parameter);
            if (parameter === 37445) return 'Intel Inc.'; // 常见显卡
            if (parameter === 37446) return 'Intel Iris OpenGL Engine'; // 常见渲染器
            return result;
        };
        
        // AudioContext 噪声
        const createOscillator = AudioContext.prototype.createOscillator;
        AudioContext.prototype.createOscillator = function() {
            const oscillator = createOscillator.call(this);
            const start = oscillator.start;
            oscillator.start = function() {
                // 可以在这里添加微小的随机频率偏移
                return start.apply(this, arguments);
            };
            return oscillator;
        };
    ";
}

internal sealed class PwBrowserSession
{
    public string Id { get; init; } = "";
    public IBrowserContext Context { get; init; } = null!;
    public IPage? Page { get; set; }
    public DateTime CreatedAt { get; init; }
}
