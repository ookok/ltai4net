using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Capability.Integration;

public sealed class TranslateConfig
{
    public string Provider { get; set; } = "";
    public string AppId { get; set; } = "";
    public string SecretKey { get; set; } = "";
}

public sealed class TranslateService
{
    private readonly HttpClient _http;
    private readonly ILogger<TranslateService> _logger;
    public TranslateConfig Config { get; set; } = new();

    public TranslateService(ILogger<TranslateService>? logger = null)
    {
        _logger = logger ?? NullLogger<TranslateService>.Instance;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<string?> TranslateAsync(string text, string from = "auto", string to = "zh")
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        return Config.Provider.ToLowerInvariant() switch
        {
            "baidu" => await BaiduTranslateAsync(text, from, to),
            _ => await BaiduTranslateAsync(text, from, to)
        };
    }

    private async Task<string?> BaiduTranslateAsync(string text, string from, string to)
    {
        if (string.IsNullOrWhiteSpace(Config.AppId) || string.IsNullOrWhiteSpace(Config.SecretKey))
            return null;

        try
        {
            var salt = RandomNumberGenerator.GetInt32(100000).ToString();
            var signStr = Config.AppId + text + salt + Config.SecretKey;
            var sign = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(signStr)));

            var url = $"https://fanyi-api.baidu.com/api/trans/vip/translate" +
                      $"?q={Uri.EscapeDataString(text)}" +
                      $"&from={from}&to={to}" +
                      $"&appid={Config.AppId}&salt={salt}&sign={sign}";

            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("error_code", out var err))
            {
                var errMsg = doc.RootElement.TryGetProperty("error_msg", out var em) ? em.GetString() : "";
                _logger.LogWarning("Baidu translate error: {Code} {Msg}", err.GetString(), errMsg);
                return null;
            }

            if (doc.RootElement.TryGetProperty("trans_result", out var results))
            {
                var parts = new List<string>();
                foreach (var r in results.EnumerateArray())
                {
                    if (r.TryGetProperty("dst", out var dst))
                        parts.Add(dst.GetString() ?? "");
                }
                return string.Join("", parts);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Baidu translate failed");
            return null;
        }
    }
}
