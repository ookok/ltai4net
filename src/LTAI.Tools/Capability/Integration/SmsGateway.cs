using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tools.Integration;

public sealed class SmsConfig
{
    public string Provider { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string SignName { get; set; } = "";
    public string TemplateCode { get; set; } = "";
    public List<string> PhoneNumbers { get; set; } = new();
    public bool Enabled { get; set; }
}

public sealed class SmsGateway : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<SmsGateway> _logger;
    public SmsConfig Config { get; set; } = new();

    public SmsGateway(ILogger<SmsGateway>? logger = null)
    {
        _logger = logger ?? NullLogger<SmsGateway>.Instance;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public void Dispose() { _http?.Dispose(); }

    public async Task<bool> SendAsync(string message, string? phone = null)
    {
        if (!Config.Enabled || string.IsNullOrWhiteSpace(Config.ApiKey))
            return false;

        var targetPhone = phone ?? Config.PhoneNumbers.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(targetPhone))
            return false;

        return Config.Provider.ToLowerInvariant() switch
        {
            "aliyun" => await SendAliyunAsync(targetPhone, message),
            "tencent" => await SendTencentAsync(targetPhone, message),
            _ => false
        };
    }

    private async Task<bool> SendAliyunAsync(string phone, string message)
    {
        try
        {
            var parameters = new Dictionary<string, string>
            {
                ["AccessKeyId"] = Config.ApiKey,
                ["Action"] = "SendSms",
                ["Format"] = "JSON",
                ["PhoneNumbers"] = phone,
                ["SignName"] = Config.SignName,
                ["TemplateCode"] = Config.TemplateCode,
                ["TemplateParam"] = JsonSerializer.Serialize(new { code = message }),
                ["SignatureMethod"] = "HMAC-SHA1",
                ["SignatureVersion"] = "1.0",
                ["SignatureNonce"] = Guid.NewGuid().ToString("N"),
                ["Timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["Version"] = "2017-05-25"
            };

            var sortedParams = parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");
            var canonical = string.Join("&", sortedParams);
            var stringToSign = $"POST&{Uri.EscapeDataString("/")}&{Uri.EscapeDataString(canonical)}";

            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(Config.ApiSecret + "&"));
            var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
            parameters["Signature"] = signature;

            var content = new FormUrlEncodedContent(parameters.Where(kv => kv.Key != "SignatureMethod" && kv.Key != "SignatureVersion"));
            var response = await _http.PostAsync("https://dysmsapi.aliyuncs.com/", content);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var doc = JsonDocument.Parse(body);
                var code = doc.RootElement.GetProperty("Code").GetString();
                return code == "OK";
            }

            _logger.LogWarning("Aliyun SMS failed: {Status} {Body}", (int)response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Aliyun SMS send failed");
            return false;
        }
    }

    private Task<bool> SendTencentAsync(string phone, string message)
    {
        return Task.FromResult(false);
    }
}
