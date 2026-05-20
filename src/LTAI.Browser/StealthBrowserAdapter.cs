using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.Browser.Interfaces;
using LTAI.Browser.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace LTAI.Browser;

public sealed class StealthBrowserConfig
{
    public string Engine { get; set; } = "auto";
    public string? ExecutablePath { get; set; }
    public string? DockerEndpoint { get; set; }
    public int CdpPort { get; set; } = 9222;
    public bool Headless { get; set; } = true;
    public bool Humanize { get; set; }
    public string[] ExtraArgs { get; set; } = Array.Empty<string>();
    public string? Proxy { get; set; }
    public int LaunchTimeoutMs { get; set; } = 30000;
}

public sealed class StealthBrowserAdapter
{
    private readonly StealthBrowserConfig _config;
    private readonly ILogger<StealthBrowserAdapter> _logger;
    private readonly ConcurrentDictionary<string, IBrowser> _remoteBrowsers = new();
    private IPlaywright? _playwright;

    public StealthBrowserAdapter(
        StealthBrowserConfig? config = null,
        ILogger<StealthBrowserAdapter>? logger = null)
    {
        _config = config ?? new StealthBrowserConfig();
        _logger = logger ?? NullLogger<StealthBrowserAdapter>.Instance;
    }

    public async Task<IBrowser> LaunchStealthBrowserAsync(CancellationToken ct = default)
    {
        _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        IBrowser browser;

        if (_config.DockerEndpoint != null)
        {
            browser = await ConnectDockerSidecarAsync(ct);
        }
        else if (_config.ExecutablePath != null)
        {
            browser = await LaunchLocalStealthBrowserAsync(ct);
        }
        else
        {
            browser = await LaunchDefaultPlaywrightAsync(ct);
        }

        _logger.LogInformation(
            "StealthBrowser: engine={Engine} connected via {Method}",
            _config.Engine, _config.DockerEndpoint != null ? "docker-cdp" : "local");

        return browser;
    }

    private async Task<IBrowser> ConnectDockerSidecarAsync(CancellationToken ct)
    {
        var uri = new Uri(_config.DockerEndpoint!);
        var cdpUrl = $"{uri.Scheme}://{uri.Host}:{_config.CdpPort}";

        _logger.LogInformation("Connecting to Docker sidecar at {Url}", cdpUrl);

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(_config.LaunchTimeoutMs) };
        var json = await httpClient.GetStringAsync(
            $"{cdpUrl}/json/version", ct);

        var cdpInfo = JsonDocument.Parse(json).RootElement;
        var wsEndpoint = cdpInfo.GetProperty("webSocketDebuggerUrl").GetString()!;

        return await _playwright!.Chromium.ConnectOverCDPAsync(wsEndpoint);
    }

    private async Task<IBrowser> LaunchLocalStealthBrowserAsync(CancellationToken ct)
    {
        var args = new List<string>
        {
            "--no-sandbox",
            "--disable-setuid-sandbox",
            "--disable-dev-shm-usage",
            "--disable-blink-features=AutomationControlled"
        };

        if (_config.Headless)
            args.Add("--headless=new");

        args.AddRange(_config.ExtraArgs);

        if (_config.Proxy != null)
            args.Add($"--proxy-server={_config.Proxy}");

        var launchOptions = new BrowserTypeLaunchOptions
        {
            Headless = _config.Headless,
            ExecutablePath = _config.ExecutablePath,
            Args = args,
            Timeout = _config.LaunchTimeoutMs
        };

        return await _playwright!.Chromium.LaunchAsync(launchOptions);
    }

    private async Task<IBrowser> LaunchDefaultPlaywrightAsync(CancellationToken ct)
    {
        return await _playwright!.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
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

    public async Task<IBrowserContext> CreateStealthContextAsync(
        IBrowser browser,
        string? profilePath = null,
        CancellationToken ct = default)
    {
        var contextOptions = new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36",
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
            Locale = "zh-CN",
            TimezoneId = "Asia/Shanghai",
            BypassCSP = true,
            IgnoreHTTPSErrors = true
        };

        if (_config.Proxy != null)
        {
            contextOptions.Proxy = new Proxy { Server = _config.Proxy };
        }

        var context = profilePath != null
            ? await browser.NewContextAsync(contextOptions)
            : await browser.NewContextAsync(contextOptions);

        await context.AddInitScriptAsync("""
            Object.defineProperty(navigator, 'webdriver', { get: () => false });
            Object.defineProperty(navigator, 'plugins', { get: () => [1,2,3,4,5] });
            Object.defineProperty(navigator, 'languages', { get: () => ['zh-CN','zh','en'] });
            window.chrome = { runtime: {} };

            const getParameter = WebGLRenderingContext.prototype.getParameter;
            WebGLRenderingContext.prototype.getParameter = function(parameter) {
                if (parameter === 37445) return 'Intel Inc.';
                if (parameter === 37446) return 'Intel Iris OpenGL Engine';
                return getParameter.call(this, parameter);
            };
            """);

        return context;
    }

    public void Dispose()
    {
        _playwright?.Dispose();
    }
}
