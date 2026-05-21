using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using LTAI.Browser.Interfaces;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace LTAI.Browser;

public sealed class StealthBrowserAdapter
{
    private readonly StealthBrowserConfig _config;
    private readonly ILogger<StealthBrowserAdapter> _logger;
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, IBrowser> _remoteBrowsers = new();
    private IPlaywright? _playwright;

    public StealthBrowserAdapter(
        IOptions<LTAIOptions> options,
        ILogger<StealthBrowserAdapter>? logger = null)
    {
        _config = options.Value.StealthBrowser;
        _logger = logger ?? NullLogger<StealthBrowserAdapter>.Instance;
        _cacheDir = Path.Combine(options.Value.DataDirectory, "browser", "stealth");
        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<IBrowser> LaunchStealthBrowserAsync(CancellationToken ct = default)
    {
        _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        IBrowser browser;

        if (!string.IsNullOrEmpty(_config.DockerImage))
        {
            browser = await ConnectDockerSidecarAsync(ct);
        }
        else if (!string.IsNullOrEmpty(_config.ExecutablePath))
        {
            browser = await LaunchLocalStealthBrowserAsync(ct);
        }
        else if (_config.AutoDownload)
        {
            var exePath = await EnsureBinaryDownloadedAsync(ct);
            if (exePath != null)
                browser = await LaunchWithBinaryAsync(exePath, ct);
            else
                browser = await LaunchDefaultPlaywrightAsync(ct);
        }
        else
        {
            browser = await LaunchDefaultPlaywrightAsync(ct);
        }

        _logger.LogInformation(
            "StealthBrowser: engine={Engine} connected", _config.Engine);

        return browser;
    }

    private async Task<string?> EnsureBinaryDownloadedAsync(CancellationToken ct)
    {
        var platforms = new Dictionary<string, string>
        {
            ["linux-x64"] = "cloakbrowser-linux-x64.tar.gz",
            ["linux-arm64"] = "cloakbrowser-linux-arm64.tar.gz",
            ["win-x64"] = "cloakbrowser-win-x64.zip",
            ["osx-arm64"] = "cloakbrowser-macos-arm64.tar.gz",
            ["osx-x64"] = "cloakbrowser-macos-x64.tar.gz"
        };

        var rid = GetRuntimeId();
        if (!platforms.TryGetValue(rid, out var archiveName))
        {
            _logger.LogWarning("No stealth binary for platform: {Rid}", rid);
            return null;
        }

        var archivePath = Path.Combine(_cacheDir, archiveName);
        var extractDir = Path.Combine(_cacheDir, rid);
        var exeName = rid.StartsWith("win") ? "cloakbrowser.exe" : "cloakbrowser";
        var exePath = Path.Combine(extractDir, exeName);

        if (File.Exists(exePath))
        {
            _logger.LogDebug("Stealth browser binary found at: {Path}", exePath);
            return exePath;
        }

        var downloadUrl = $"{_config.DownloadUrl}/{archiveName}";
        _logger.LogInformation("Downloading stealth browser from: {Url}", downloadUrl);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode && !string.IsNullOrEmpty(_config.DownloadMirror))
            {
                downloadUrl = $"{_config.DownloadMirror}/{archiveName}";
                _logger.LogInformation("Falling back to mirror: {Url}", downloadUrl);
                response = await http.GetAsync(downloadUrl, ct);
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = File.Create(archivePath);
            await stream.CopyToAsync(fileStream, ct);

            Directory.CreateDirectory(extractDir);
            await ExtractArchiveAsync(archivePath, extractDir, ct);

            if (File.Exists(exePath))
                return exePath;

            return Directory.GetFiles(extractDir, exeName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download stealth browser binary");
            return null;
        }
    }

    private static async Task ExtractArchiveAsync(string archivePath, string extractDir, CancellationToken ct)
    {
        if (archivePath.EndsWith(".tar.gz"))
        {
            var psi = new System.Diagnostics.ProcessStartInfo("tar", $"-xzf \"{archivePath}\" -C \"{extractDir}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            var proc = System.Diagnostics.Process.Start(psi)!;
            await proc.WaitForExitAsync(ct);
        }
        else if (archivePath.EndsWith(".zip"))
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, extractDir, true);
        }
    }

    private static string GetRuntimeId()
    {
        if (OperatingSystem.IsLinux())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        if (OperatingSystem.IsWindows())
            return "win-x64";
        if (OperatingSystem.IsMacOS())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        return "linux-x64";
    }

    private async Task<IBrowser> LaunchWithBinaryAsync(string exePath, CancellationToken ct)
    {
        var args = new List<string>
        {
            "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage",
            "--disable-blink-features=AutomationControlled"
        };

        if (_config.Headless) args.Add("--headless=new");
        args.AddRange(_config.ExtraArgs);
        if (_config.Proxy != null) args.Add($"--proxy-server={_config.Proxy}");

        return await _playwright!.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _config.Headless,
            ExecutablePath = exePath,
            Args = args,
            Timeout = _config.LaunchTimeoutMs
        });
    }

    private async Task<IBrowser> ConnectDockerSidecarAsync(CancellationToken ct)
    {
        var cdpUrl = $"http://localhost:{_config.CdpPort}";

        _logger.LogInformation("Connecting to Docker sidecar at {Url}", cdpUrl);

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(_config.LaunchTimeoutMs) };
        var json = await httpClient.GetStringAsync($"{cdpUrl}/json/version", ct);

        var cdpInfo = JsonDocument.Parse(json).RootElement;
        var wsEndpoint = cdpInfo.GetProperty("webSocketDebuggerUrl").GetString()!;

        return await _playwright!.Chromium.ConnectOverCDPAsync(wsEndpoint);
    }

    private async Task<IBrowser> LaunchLocalStealthBrowserAsync(CancellationToken ct)
    {
        var args = new List<string>
        {
            "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage",
            "--disable-blink-features=AutomationControlled"
        };

        if (_config.Headless) args.Add("--headless=new");
        args.AddRange(_config.ExtraArgs);
        if (_config.Proxy != null) args.Add($"--proxy-server={_config.Proxy}");

        return await _playwright!.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _config.Headless,
            ExecutablePath = _config.ExecutablePath,
            Args = args,
            Timeout = _config.LaunchTimeoutMs
        });
    }

    private async Task<IBrowser> LaunchDefaultPlaywrightAsync(CancellationToken ct)
    {
        return await _playwright!.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[]
            {
                "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage",
                "--disable-blink-features=AutomationControlled"
            }
        });
    }

    public async Task<IBrowserContext> CreateStealthContextAsync(
        IBrowser browser, string? profilePath = null, CancellationToken ct = default)
    {
        var contextOptions = new BrowserNewContextOptions
        {
            UserAgent = _config.UserAgent,
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
            Locale = "zh-CN",
            TimezoneId = "Asia/Shanghai",
            BypassCSP = true,
            IgnoreHTTPSErrors = true
        };

        if (_config.Proxy != null)
            contextOptions.Proxy = new Proxy { Server = _config.Proxy };

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
