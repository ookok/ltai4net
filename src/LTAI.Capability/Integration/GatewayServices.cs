using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LTAI.Capability.Integration;

public sealed class TelegramBot
{
    private readonly HttpClient _http;
    private readonly ILogger<TelegramBot> _logger;
    private readonly string _token;

    public TelegramBot(ILogger<TelegramBot> logger, string? token = null)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.BaseAddress = new Uri("https://api.telegram.org/");
        _logger = logger;
        _token = token ?? Environment.GetEnvironmentVariable("LTAI_TELEGRAM_TOKEN") ?? "";
    }

    public async Task<bool> SendMessageAsync(long chatId, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token)) return false;
        try
        {
            var payload = JsonSerializer.Serialize(new { chat_id = chatId, text, parse_mode = "Markdown" });
            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"bot{_token}/sendMessage", content, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Telegram send failed"); return false; }
    }

    public async Task<bool> SendMarkdownAsync(long chatId, string markdown, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token)) return false;
        try
        {
            var payload = JsonSerializer.Serialize(new { chat_id = chatId, text = markdown[..Math.Min(markdown.Length, 4000)], parse_mode = "MarkdownV2" });
            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"bot{_token}/sendMessage", content, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Telegram markdown send failed"); return false; }
    }

    public async Task<bool> SendCodeBlockAsync(long chatId, string code, string language = "", CancellationToken ct = default)
    {
        var msg = $"```{language}\n{code[..Math.Min(code.Length, 3500)]}\n```";
        return await SendMessageAsync(chatId, msg, ct);
    }

    public async Task<string?> GetUpdatesAsync(long offset = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token)) return null;
        try
        {
            return await _http.GetStringAsync($"bot{_token}/getUpdates?offset={offset}&timeout=30", ct);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Telegram getUpdates failed"); return null; }
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_token);
}

public sealed class WechatWorkNotifier
{
    private readonly HttpClient _http;
    private readonly ILogger<WechatWorkNotifier> _logger;
    private readonly string _webhookUrl;

    public WechatWorkNotifier(ILogger<WechatWorkNotifier> logger, string? webhookUrl = null)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _logger = logger;
        _webhookUrl = webhookUrl ?? Environment.GetEnvironmentVariable("LTAI_WEWORK_WEBHOOK") ?? "";
    }

    public async Task<bool> SendTextAsync(string content, List<string>? mentionedList = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_webhookUrl)) return false;
        try
        {
            var payload = new
            {
                msgtype = "text",
                text = new { content = content[..Math.Min(content.Length, 2000)], mentioned_list = mentionedList ?? new List<string>() }
            };
            var json = JsonSerializer.Serialize(payload);
            var resp = await _http.PostAsync(_webhookUrl, new StringContent(json, System.Text.Encoding.UTF8, "application/json"), ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "WeWork notify failed"); return false; }
    }

    public async Task<bool> SendMarkdownAsync(string content, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_webhookUrl)) return false;
        try
        {
            var payload = new { msgtype = "markdown", markdown = new { content = content[..Math.Min(content.Length, 4000)] } };
            var json = JsonSerializer.Serialize(payload);
            var resp = await _http.PostAsync(_webhookUrl, new StringContent(json, System.Text.Encoding.UTF8, "application/json"), ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "WeWork markdown failed"); return false; }
    }

    public async Task<bool> SendReviewReportAsync(string report, CancellationToken ct = default)
    {
        var msg = $"## Code Review Report\n\n{report[..Math.Min(report.Length, 3800)]}";
        return await SendMarkdownAsync(msg, ct);
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_webhookUrl);
}

public sealed class AutoUpdater
{
    private readonly ILogger<AutoUpdater> _logger;
    private readonly string _updateUrl;
    private readonly string _currentVersion;

    public AutoUpdater(ILogger<AutoUpdater> logger, string? updateUrl = null)
    {
        _logger = logger;
        _currentVersion = "5.5.0";
        _updateUrl = updateUrl ?? Environment.GetEnvironmentVariable("LTAI_UPDATE_URL") ?? "";
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_updateUrl))
            return new UpdateCheckResult { HasUpdate = false, CurrentVersion = _currentVersion };

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = await http.GetStringAsync(_updateUrl, ct);
            var doc = JsonDocument.Parse(json);
            var latestVersion = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            var downloadUrl = doc.RootElement.TryGetProperty("download_url", out var d) ? d.GetString() ?? "" : "";
            var releaseNotes = doc.RootElement.TryGetProperty("release_notes", out var r) ? r.GetString() ?? "" : "";

            return new UpdateCheckResult
            {
                HasUpdate = !string.IsNullOrEmpty(latestVersion) && latestVersion != _currentVersion,
                CurrentVersion = _currentVersion,
                LatestVersion = latestVersion,
                DownloadUrl = downloadUrl,
                ReleaseNotes = releaseNotes
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update check failed");
            return new UpdateCheckResult { HasUpdate = false, CurrentVersion = _currentVersion };
        }
    }

    public async Task<bool> DownloadUpdateAsync(string downloadUrl, string savePath, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var bytes = await http.GetByteArrayAsync(downloadUrl, ct);
            await File.WriteAllBytesAsync(savePath, bytes, ct);
            _logger.LogInformation("Update downloaded: {Size} bytes to {Path}", bytes.Length, savePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update download failed");
            return false;
        }
    }
}

public sealed class UpdateCheckResult
{
    public bool HasUpdate { get; init; }
    public string CurrentVersion { get; init; } = "";
    public string LatestVersion { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string ReleaseNotes { get; init; } = "";
}

public sealed class UnifiedNotifier
{
    private readonly ILogger<UnifiedNotifier> _logger;
    private readonly TelegramBot? _telegram;
    private readonly WechatWorkNotifier? _wework;

    public UnifiedNotifier(ILogger<UnifiedNotifier> logger, TelegramBot? telegram = null, WechatWorkNotifier? wework = null)
    {
        _logger = logger;
        _telegram = telegram;
        _wework = wework;
    }

    public async Task NotifyAllAsync(string message, CancellationToken ct = default)
    {
        var tasks = new List<Task<bool>>();
        if (_telegram?.IsConfigured == true) tasks.Add(_telegram.SendMessageAsync(0, message, ct));
        if (_wework?.IsConfigured == true) tasks.Add(_wework.SendTextAsync(message, ct: ct));
        if (tasks.Count > 0) await Task.WhenAll(tasks);
        _logger.LogInformation("Notified {Count} channels", tasks.Count);
    }

    public async Task NotifyReviewAsync(string report, CancellationToken ct = default)
    {
        if (_wework?.IsConfigured == true)
            await _wework.SendReviewReportAsync(report, ct);
        if (_telegram?.IsConfigured == true)
            await _telegram.SendMarkdownAsync(0, report, ct);
    }
}
