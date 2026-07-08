using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LTAI.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

public sealed record ImChannelConfig(
    string Type,
    string? BotToken,
    string? Endpoint,
    bool Enabled);

[ToolDomain("communication")]
public sealed class ImChannelTool : IDisposable
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, ImChannelConfig> _channels;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _cts = new();

    public ImChannelTool(
        IHttpClientFactory? httpFactory = null,
        ILogger? logger = null)
    {
        _http = httpFactory?.CreateClient("IM") ?? new HttpClient();
        _logger = logger;
        _channels = LoadConfig();
    }

    [Description("发送消息到 IM 通道（Telegram/Slack/飞书/钉钉/微信等）")]
    [return: Description("发送结果")]
    public async Task<string> SendMessage(
        [Description("通道类型：telegram/slack/feishu/dingtalk/wechat")] string channelType,
        [Description("目标（用户ID/群组ID/频道ID）")] string target,
        [Description("消息内容")] string message)
    {
        if (!_channels.TryGetValue(channelType.ToLowerInvariant(), out var config) || !config.Enabled)
            return $"Error: channel '{channelType}' not configured or disabled";

        try
        {
            return channelType.ToLowerInvariant() switch
            {
                "telegram" => await SendTelegramAsync(config, target, message).ConfigureAwait(false),
                "slack" => await SendSlackAsync(config, target, message).ConfigureAwait(false),
                "feishu" => await SendFeishuAsync(config, target, message).ConfigureAwait(false),
                "dingtalk" => await SendDingTalkAsync(config, target, message).ConfigureAwait(false),
                "wechat" => await SendWeChatAsync(config, target, message).ConfigureAwait(false),
                _ => $"Error: unsupported channel '{channelType}'"
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "IMChannelTool: failed to send to {Channel}", channelType);
            return $"Error sending to {channelType}: {ex.Message}";
        }
    }

    [Description("检查已配置的 IM 通道状态")]
    [return: Description("各通道的启用/禁用状态")]
    public string ListChannels()
    {
        var sb = new StringBuilder("## IM Channels\n\n");
        foreach (var (name, cfg) in _channels)
        {
            var status = cfg.Enabled ? "✅ enabled" : "❌ disabled";
            sb.AppendLine($"- **{name}**: {status}");
        }
        return sb.ToString();
    }

    private async Task<string> SendTelegramAsync(ImChannelConfig cfg, string chatId, string message)
    {
        if (cfg.BotToken == null) return "Error: TELEGRAM_BOT_TOKEN not configured";
        var url = $"https://api.telegram.org/bot{cfg.BotToken}/sendMessage";
        var payload = new { chat_id = chatId, text = message };
        var resp = await _http.PostAsJsonAsync(url, payload, _cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return $"Message sent to Telegram chat {chatId}";
    }

    private async Task<string> SendSlackAsync(ImChannelConfig cfg, string channel, string message)
    {
        if (cfg.BotToken == null) return "Error: SLACK_BOT_TOKEN not configured";
        var url = "https://slack.com/api/chat.postMessage";
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.BotToken);
        req.Content = JsonContent.Create(new { channel, text = message });
        var resp = await _http.SendAsync(req, _cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return $"Message sent to Slack channel {channel}";
    }

    private async Task<string> SendFeishuAsync(ImChannelConfig cfg, string openId, string message)
    {
        var url = "https://open.feishu.cn/open-apis/im/v1/messages";
        var payload = new { receive_id = openId, msg_type = "text", content = JsonSerializer.Serialize(new { text = message }) };
        var token = await GetFeishuTokenAsync(cfg).ConfigureAwait(false);
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Content = JsonContent.Create(payload);
        var resp = await _http.SendAsync(req, _cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return $"Message sent to Feishu user {openId}";
    }

    private async Task<string> SendDingTalkAsync(ImChannelConfig cfg, string userId, string message)
    {
        if (cfg.Endpoint == null) return "Error: DINGTALK_WEBHOOK not configured";
        var payload = new { msgtype = "text", text = new { content = message } };
        var resp = await _http.PostAsJsonAsync(cfg.Endpoint, payload, _cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return $"Message sent to DingTalk webhook";
    }

    private async Task<string> SendWeChatAsync(ImChannelConfig cfg, string userId, string message)
    {
        return "Error: WeChat channel is not implemented (iLink SDK integration pending)";
    }

    private async Task<string> GetFeishuTokenAsync(ImChannelConfig cfg)
    {
        var url = "https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal";
        var payload = new { app_id = Environment.GetEnvironmentVariable("FEISHU_APP_ID") ?? "", app_secret = Environment.GetEnvironmentVariable("FEISHU_APP_SECRET") ?? "" };
        var resp = await _http.PostAsJsonAsync(url, payload, _cts.Token).ConfigureAwait(false);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(_cts.Token).ConfigureAwait(false);
        return json.GetProperty("tenant_access_token").GetString() ?? "";
    }

    private static Dictionary<string, ImChannelConfig> LoadConfig()
    {
        return new Dictionary<string, ImChannelConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["telegram"] = new("telegram", Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"), null, !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"))),
            ["slack"] = new("slack", Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN"), null, !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN"))),
            ["feishu"] = new("feishu", null, null, !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FEISHU_APP_ID"))),
            ["dingtalk"] = new("dingtalk", null, Environment.GetEnvironmentVariable("DINGTALK_WEBHOOK"), !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DINGTALK_WEBHOOK"))),
            ["wechat"] = new("wechat", null, null, !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WECHAT_BOT_TOKEN"))),
        };
    }

    public void Dispose() => _cts.Cancel();
}
