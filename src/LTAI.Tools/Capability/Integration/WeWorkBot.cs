using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tools.Integration;

public sealed class WeWorkBot
{
    public static readonly Lazy<WeWorkBot> Instance = new(() => new WeWorkBot());

    private readonly ILogger<WeWorkBot> _logger;
    private readonly HttpClient _http;
    private string _webhookUrl = "";
    private Func<string, string, Task<string>>? _llmCallback;
    private string _token = "";
    private string _encodingAesKey = "";
    private string _corpId = "";

    private WeWorkBot(ILogger<WeWorkBot>? logger = null)
    {
        _logger = logger ?? NullLogger<WeWorkBot>.Instance;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public void Configure(string token, string encodingAesKey, string corpId, string? webhookUrl = null)
    {
        _token = token;
        _encodingAesKey = encodingAesKey;
        _corpId = corpId;
        _webhookUrl = webhookUrl ?? Environment.GetEnvironmentVariable("LTAI_WEWORK_WEBHOOK") ?? "";

        WXBizMsgCrypt.Instance.Value.Configure(token, encodingAesKey, corpId);
    }

    public void SetLlmCallback(Func<string, string, Task<string>> callback)
    {
        _llmCallback = callback;
    }

    public string VerifyUrl(string signature, string timestamp, string nonce, string echostr)
    {
        if (!WXBizMsgCrypt.Instance.Value.VerifySignature(signature, timestamp, nonce, echostr))
            return "";

        try
        {
            return WXBizMsgCrypt.Instance.Value.DecryptMsg(signature, timestamp, nonce, echostr);
        }
        catch
        {
            return "";
        }
    }

    public async Task<string?> HandleMessageAsync(string xmlBody)
    {
        try
        {
            var fields = ParseMessageXml(xmlBody);
            if (fields.Count == 0) return null;

            var msgType = fields.GetValueOrDefault("MsgType", "");
            var content = fields.GetValueOrDefault("Content", "");
            var fromUser = fields.GetValueOrDefault("FromUserName", "");
            var toUser = fields.GetValueOrDefault("ToUserName", "");

            if (msgType != "text" || string.IsNullOrEmpty(content))
            {
                _logger.LogDebug("Non-text WeWork message, type: {MsgType}", msgType);
                return BuildTextReply(toUser, fromUser, "目前仅支持文本消息");
            }

            string reply;
            if (_llmCallback != null)
            {
                reply = await _llmCallback(content, fromUser);
            }
            else
            {
                reply = $"收到消息: {content[..Math.Min(content.Length, 100)]}";
            }

            return BuildTextReply(toUser, fromUser, reply);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WeWork message handling failed");
            return null;
        }
    }

    public Dictionary<string, string> ParseMessageXml(string xml)
    {
        var result = new Dictionary<string, string>();
        try
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root;
            if (root == null) return result;

            string[] fields = { "MsgType", "Content", "FromUserName", "ToUserName", "CreateTime", "MsgId", "Event", "EventKey", "AgentID" };
            foreach (var field in fields)
            {
                var element = root.Element(field);
                if (element != null)
                    result[field] = element.Value;
            }
        }
        catch
        {
            _logger.LogWarning("Failed to parse WeWork XML message");
        }

        return result;
    }

    public static string BuildTextReply(string toUser, string fromUser, string content)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        return $@"<xml>
<ToUserName><![CDATA[{EscapeXml(fromUser)}]]></ToUserName>
<FromUserName><![CDATA[{EscapeXml(toUser)}]]></FromUserName>
<CreateTime>{timestamp}</CreateTime>
<MsgType><![CDATA[text]]></MsgType>
<Content><![CDATA[{EscapeXml(content)}]]></Content>
</xml>";
    }

    public string BuildNewsReply(string toUser, string fromUser, List<(string Title, string Description, string PicUrl, string Url)> articles)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var sb = new StringBuilder();
        sb.AppendLine("<xml>");
        sb.AppendLine($"<ToUserName><![CDATA[{EscapeXml(fromUser)}]]></ToUserName>");
        sb.AppendLine($"<FromUserName><![CDATA[{EscapeXml(toUser)}]]></FromUserName>");
        sb.AppendLine($"<CreateTime>{timestamp}</CreateTime>");
        sb.AppendLine("<MsgType><![CDATA[news]]></MsgType>");
        sb.AppendLine($"<ArticleCount>{articles.Count}</ArticleCount>");
        sb.AppendLine("<Articles>");

        foreach (var (title, description, picUrl, url) in articles)
        {
            sb.AppendLine("<item>");
            sb.AppendLine($"<Title><![CDATA[{EscapeXml(title)}]]></Title>");
            sb.AppendLine($"<Description><![CDATA[{EscapeXml(description)}]]></Description>");
            sb.AppendLine($"<PicUrl><![CDATA[{EscapeXml(picUrl)}]]></PicUrl>");
            sb.AppendLine($"<Url><![CDATA[{EscapeXml(url)}]]></Url>");
            sb.AppendLine("</item>");
        }

        sb.AppendLine("</Articles>");
        sb.AppendLine("</xml>");

        return sb.ToString();
    }

    public async Task<bool> SendWebhookAsync(string content, List<string>? mentionedList = null)
    {
        if (string.IsNullOrEmpty(_webhookUrl))
        {
            _logger.LogWarning("WeWork webhook URL not configured");
            return false;
        }

        try
        {
            var payload = new
            {
                msgtype = "text",
                text = new
                {
                    content = content[..Math.Min(content.Length, 2000)],
                    mentioned_list = mentionedList ?? new List<string>()
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var resp = await _http.PostAsync(
                _webhookUrl,
                new StringContent(json, Encoding.UTF8, "application/json"));

            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WeWork webhook send failed");
            return false;
        }
    }

    private static string EscapeXml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}
