using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tools.Integration;

public record GatewayMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("subject")] string? Subject,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("sent_at")] DateTime? SentAt,
    [property: JsonPropertyName("error")] string? Error)
{
    public static GatewayMessage Create(string platform, string to, string body, string? subject = null)
    {
        return new GatewayMessage(
            Guid.NewGuid().ToString("N")[..12],
            platform,
            to,
            subject,
            body,
            "pending",
            null,
            null);
    }
}

public sealed class MessageGateway
{
    public static readonly Lazy<MessageGateway> Instance = new(() => new MessageGateway());

    private readonly ConcurrentDictionary<string, GatewayMessage> _messages = new();
    private readonly Lock _lock = new();
    private readonly HttpClient _http;
    private readonly ILogger<MessageGateway> _logger;

    private string _smtpHost = "";
    private int _smtpPort = 25;
    private string _smtpUser = "";
    private string _smtpPass = "";
    private bool _smtpUseSsl;

    private MessageGateway(ILogger<MessageGateway>? logger = null)
    {
        _logger = logger ?? NullLogger<MessageGateway>.Instance;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void ConfigureSmtp(string host, int port, string user, string pass, bool useSsl = true)
    {
        _smtpHost = host;
        _smtpPort = port;
        _smtpUser = user;
        _smtpPass = pass;
        _smtpUseSsl = useSsl;
    }

    public async Task<GatewayMessage> SendAsync(GatewayMessage message)
    {
        _messages[message.Id] = message;

        try
        {
            var updated = message.Platform switch
            {
                "telegram" => await SendTelegramInternal(message),
                "smtp" => await SendSmtpInternal(message),
                "cli" => await SendCliInternal(message),
                "discord" => await SendDiscordInternal(message),
                _ => message with { Status = "failed", Error = $"Unknown platform: {message.Platform}" }
            };

            _messages[message.Id] = updated;
            return updated;
        }
        catch (Exception ex)
        {
            var failed = message with { Status = "failed", Error = ex.Message };
            _messages[message.Id] = failed;
            _logger.LogError(ex, "Message send failed: {Platform} {Id}", message.Platform, message.Id);
            return failed;
        }
    }

    private async Task<GatewayMessage> SendTelegramInternal(GatewayMessage message)
    {
        var token = Environment.GetEnvironmentVariable("LTAI_TELEGRAM_TOKEN") ?? "";
        if (string.IsNullOrEmpty(token))
            return message with { Status = "failed", Error = "Telegram token not configured" };

        var payload = JsonSerializer.Serialize(new
        {
            chat_id = message.To,
            text = message.Body,
            parse_mode = "Markdown"
        });

        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", content);
        var success = resp.IsSuccessStatusCode;

        return message with
        {
            Status = success ? "sent" : "failed",
            SentAt = success ? DateTime.UtcNow : null,
            Error = success ? null : $"HTTP {(int)resp.StatusCode}"
        };
    }

    private async Task<GatewayMessage> SendSmtpInternal(GatewayMessage message)
    {
        if (string.IsNullOrEmpty(_smtpHost))
            return message with { Status = "failed", Error = "SMTP not configured" };

        try
        {
            using var smtp = new System.Net.Mail.SmtpClient(_smtpHost, _smtpPort);
            smtp.EnableSsl = _smtpUseSsl;
            if (!string.IsNullOrEmpty(_smtpUser))
            {
                smtp.UseDefaultCredentials = false;
                smtp.Credentials = new System.Net.NetworkCredential(_smtpUser, _smtpPass);
            }
            else
            {
                smtp.UseDefaultCredentials = true;
            }

            smtp.Timeout = 30000;

            using var mail = new System.Net.Mail.MailMessage
            {
                From = new System.Net.Mail.MailAddress(_smtpUser, "LTAI Gateway"),
                Subject = message.Subject ?? "(no subject)",
                Body = message.Body,
                IsBodyHtml = false
            };
            mail.To.Add(message.To);

            await smtp.SendMailAsync(mail);

            return message with { Status = "sent", SentAt = DateTime.UtcNow };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SMTP send failed to {To}", message.To);
            return message with { Status = "failed", Error = ex.Message };
        }
    }

    private Task<GatewayMessage> SendCliInternal(GatewayMessage message)
    {
        _logger.LogInformation(
            "[{Platform}] Message → {To} Subject: {Subject} Body: {Body}",
            message.Platform.ToUpperInvariant(), message.To, message.Subject ?? "",
            message.Body?[..Math.Min(message.Body.Length, 200)] ?? "");

        return Task.FromResult(message with { Status = "sent", SentAt = DateTime.UtcNow });
    }

    private Task<GatewayMessage> SendDiscordInternal(GatewayMessage message)
    {
        return Task.FromResult(message with
        {
            Status = "failed",
            Error = "Discord integration not yet implemented"
        });
    }

    public async Task<bool> SendTelegramAsync(string chatId, string text)
    {
        var msg = GatewayMessage.Create("telegram", chatId, text);
        var result = await SendTelegramInternal(msg);
        return result.Status == "sent";
    }

    public async Task<bool> SendSmtpAsync(string to, string subject, string body)
    {
        var msg = GatewayMessage.Create("smtp", to, body, subject);
        var result = await SendSmtpInternal(msg);
        return result.Status == "sent";
    }

    public Task SendCliAsync(string message)
    {
        var msg = GatewayMessage.Create("cli", "console", message);
        SendCliInternal(msg);
        return Task.CompletedTask;
    }

    public IReadOnlyList<GatewayMessage> GetPending()
    {
        lock (_lock)
        {
            return _messages.Values.Where(m => m.Status == "pending").ToList();
        }
    }

    public Dictionary<string, int> GetStats()
    {
        lock (_lock)
        {
            var values = _messages.Values.ToList();
            return new Dictionary<string, int>
            {
                ["total"] = values.Count,
                ["pending"] = values.Count(m => m.Status == "pending"),
                ["sent"] = values.Count(m => m.Status == "sent"),
                ["failed"] = values.Count(m => m.Status == "failed")
            };
        }
    }
}
