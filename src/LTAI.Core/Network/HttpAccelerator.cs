using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Network;

public sealed class HttpAcceleratorConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("proxy_port")]
    public int ProxyPort { get; set; } = 0;

    [JsonPropertyName("mirrors")]
    public Dictionary<string, AcceleratorMirror> Mirrors { get; set; } = new();

    [JsonPropertyName("timeout_ms")]
    public int TimeoutMs { get; set; } = 300000;

    [JsonPropertyName("retry_count")]
    public int RetryCount { get; set; } = 3;

    [JsonPropertyName("cache_ttl_seconds")]
    public int CacheTtlSeconds { get; set; } = 3600;
}

public sealed class AcceleratorMirror
{
    [JsonPropertyName("domain")]
    public string Domain { get; set; } = "";

    [JsonPropertyName("replace_host")]
    public string? ReplaceHost { get; set; }

    [JsonPropertyName("prefix_path")]
    public string? PrefixPath { get; set; }

    [JsonPropertyName("direct_urls")]
    public List<string> DirectUrls { get; set; } = new();

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}

public sealed class HttpAccelerator : DelegatingHandler
{
    private readonly HttpAcceleratorConfig _config;
    private readonly ILogger<HttpAccelerator> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _rateLimiter = new();

    public static HttpAcceleratorConfig DefaultConfig => new()
    {
        Enabled = true,
        Mirrors = new Dictionary<string, AcceleratorMirror>
        {
            ["github"] = new()
            {
                Domain = "github.com",
                DirectUrls = new() { "https://ghproxy.com/https://github.com", "https://ghproxy.net/https://github.com" },
                Description = "GitHub 文件下载加速"
            },
            ["github-api"] = new()
            {
                Domain = "api.github.com",
                ReplaceHost = "hub.fastgit.xyz",
                Description = "GitHub API 加速"
            },
            ["github-releases"] = new()
            {
                Domain = "github-releases.githubusercontent.com",
                DirectUrls = new() { "https://ghproxy.com/https://github.com" },
                Description = "GitHub Releases 下载加速"
            },
            ["pypi"] = new()
            {
                Domain = "pypi.org",
                ReplaceHost = "pypi.tuna.tsinghua.edu.cn",
                PrefixPath = "simple",
                Description = "PyPI 清华镜像"
            },
            ["pypi-files"] = new()
            {
                Domain = "files.pythonhosted.org",
                DirectUrls = new() { "https://pypi.tuna.tsinghua.edu.cn/packages" },
                Description = "PyPI 文件下载 清华镜像"
            },
            ["npm"] = new()
            {
                Domain = "registry.npmjs.org",
                ReplaceHost = "registry.npmmirror.com",
                Description = "npm 淘宝镜像"
            },
            ["nuget"] = new()
            {
                Domain = "api.nuget.org",
                ReplaceHost = "nuget.cdn.azure.cn",
                Description = "NuGet Azure CDN"
            },
            ["docker"] = new()
            {
                Domain = "registry-1.docker.io",
                ReplaceHost = "docker.mirrors.ustc.edu.cn",
                Description = "Docker Hub 中科大镜像"
            },
            ["rust"] = new()
            {
                Domain = "crates.io",
                ReplaceHost = "crates-io.proxy.ustclug.org",
                Description = "Rust Crates 中科大镜像"
            },
            ["flutter"] = new()
            {
                Domain = "storage.googleapis.com",
                PrefixPath = "flutter_infra_release/releases",
                ReplaceHost = "storage.flutter-io.cn",
                Description = "Flutter SDK 清华镜像"
            },
            ["cdnjs"] = new()
            {
                Domain = "cdnjs.cloudflare.com",
                ReplaceHost = "cdnjs.loli.net",
                Description = "CDNJS 加速"
            },
            ["stackoverflow"] = new()
            {
                Domain = "stackoverflow.com",
                ReplaceHost = "stackoverflow.com",
                Description = "Stack Overflow (直连)"
            },
            ["arxiv"] = new()
            {
                Domain = "arxiv.org",
                ReplaceHost = "xxx.itp.ac.cn",
                Description = "arXiv 中科院镜像"
            },
            ["claude"] = new()
            {
                Domain = "docs.anthropic.com",
                DirectUrls = new() { "https://docs.anthropic.com" },
                Description = "Anthropic 文档"
            },
            ["openai"] = new()
            {
                Domain = "platform.openai.com",
                DirectUrls = new() { "https://platform.openai.com" },
                Description = "OpenAI 文档"
            },
            ["cloakbrowser"] = new()
            {
                Domain = "github.com/CloakHQ",
                DirectUrls = new()
                {
                    "https://ghproxy.com/https://github.com/CloakHQ/CloakBrowser/releases/download",
                    "https://gitee.com/mirrors/cloakbrowser/releases/download"
                },
                Description = "CloakBrowser 隐身浏览器下载加速"
            }
        }
    };

    public HttpAccelerator(
        HttpAcceleratorConfig? config = null,
        HttpMessageHandler innerHandler = null!,
        ILogger<HttpAccelerator>? logger = null) : base(innerHandler ?? new HttpClientHandler())
    {
        _config = config ?? DefaultConfig;
        _logger = logger ?? NullLogger<HttpAccelerator>.Instance;
    }

    public static HttpClient CreateAcceleratedClient(
        HttpAcceleratorConfig? config = null,
        ILogger<HttpAccelerator>? logger = null)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        };
        var accelerator = new HttpAccelerator(config, handler, logger);
        return new HttpClient(accelerator)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_config.Enabled || request.RequestUri == null)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var host = request.RequestUri.Host.ToLowerInvariant();
        var mirror = FindMirror(host, request.RequestUri.ToString());

        if (mirror == null)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var startTime = DateTime.UtcNow;
        Exception? lastError = null;

        if (mirror.DirectUrls.Count > 0)
        {
            foreach (var directUrl in mirror.DirectUrls)
            {
                try
                {
                    var redirected = new HttpRequestMessage(request.Method, directUrl + request.RequestUri.PathAndQuery);
                    CopyHeaders(request, redirected);
                    var response = await base.SendAsync(redirected, cancellationToken).ConfigureAwait(false);

                    var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
                    _logger.LogDebug("HttpAccelerator: {Host} → direct {Url} ({Elapsed:F0}ms)",
                        host, directUrl[..Math.Min(60, directUrl.Length)], elapsed);

                    return response;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _logger.LogDebug("HttpAccelerator: direct URL failed {Url}: {Error}",
                        directUrl, ex.Message);
                }
            }
        }

        if (mirror.ReplaceHost != null)
        {
            try
            {
                var builder = new UriBuilder(request.RequestUri) { Host = mirror.ReplaceHost };
                if (mirror.PrefixPath != null)
                    builder.Path = $"/{mirror.PrefixPath}{builder.Path}";

                var redirected = new HttpRequestMessage(request.Method, builder.Uri);
                CopyHeaders(request, redirected);
                var response = await base.SendAsync(redirected, cancellationToken).ConfigureAwait(false);

                var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogDebug("HttpAccelerator: {Host} → {Mirror} ({Elapsed:F0}ms)",
                    host, mirror.ReplaceHost, elapsed);

                return response;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogDebug("HttpAccelerator: replace host failed: {Error}", ex.Message);
            }
        }

        if (lastError != null)
        {
            _logger.LogWarning("HttpAccelerator: all mirrors failed for {Url}, trying direct", request.RequestUri);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private AcceleratorMirror? FindMirror(string host, string fullUrl)
    {
        foreach (var (_, mirror) in _config.Mirrors)
        {
            if (fullUrl.Contains(mirror.Domain, StringComparison.OrdinalIgnoreCase))
                return mirror;

            if (host.Contains(mirror.Domain, StringComparison.OrdinalIgnoreCase))
                return mirror;
        }

        return null;
    }

    private static void CopyHeaders(HttpRequestMessage source, HttpRequestMessage target)
    {
        foreach (var (key, values) in source.Headers)
            target.Headers.TryAddWithoutValidation(key, values);
    }
}
