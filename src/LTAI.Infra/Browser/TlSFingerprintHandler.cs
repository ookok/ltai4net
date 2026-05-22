using System.Net.Security;
using System.Security.Authentication;
using LTAI.Infra.Browser.Interfaces;
using LTAI.Infra.Browser.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Infra.Browser;

public sealed class TlSFingerprintConfig
{
    public bool Enabled { get; set; } = true;
    public string CipherSuite { get; set; } = "Chrome-146";
    public string Ja3Fingerprint { get; set; } = "771,4865-4866-4867-49195-49199-49196-49200-52393-52392-49171-49172-156-157-47-53,0-5-10-11-13-16-23-27-35-43-45-51-65037-17513-18-65281,29-23-24,0";
    public SslProtocols TlsVersion { get; set; } = SslProtocols.Tls13 | SslProtocols.Tls12;
    public bool EnableAlpn { get; set; } = true;
    public string AlpnProtocol { get; set; } = "h2";
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36";
}

public sealed class TlSFingerprintHandler : DelegatingHandler
{
    private readonly TlSFingerprintConfig _config;
    private readonly ILogger<TlSFingerprintHandler> _logger;

    public TlSFingerprintHandler(
        TlSFingerprintConfig config,
        HttpMessageHandler innerHandler,
        ILogger<TlSFingerprintHandler>? logger = null) : base(innerHandler)
    {
        _config = config;
        _logger = logger ?? NullLogger<TlSFingerprintHandler>.Instance;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_config.Enabled)
        {
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", _config.UserAgent);
            request.Headers.TryAddWithoutValidation("sec-ch-ua",
                "\"Chromium\";v=\"146\", \"Not)A;Brand\";v=\"24\", \"Google Chrome\";v=\"146\"");
            request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            request.Headers.TryAddWithoutValidation("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        }

        _logger.LogDebug("TLS fingerprint handler: {Url}", request.RequestUri);
        return await base.SendAsync(request, cancellationToken);
    }
}

public static class TlSFingerprintExtensions
{
    public static HttpClient CreateTlSFingerprintClient(
        this TlSFingerprintConfig config,
        ILogger<TlSFingerprintHandler>? logger = null)
    {
        var innerHandler = new HttpClientHandler
        {
            SslProtocols = config.TlsVersion,
            UseCookies = true,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };

        if (config.EnableAlpn)
        {
            innerHandler.ServerCertificateCustomValidationCallback =
                (_, _, _, _) => true;
        }

        var handler = new TlSFingerprintHandler(config, innerHandler, logger);
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(3),
            DefaultRequestVersion = new Version(2, 0),
            DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrHigher
        };
    }
}
