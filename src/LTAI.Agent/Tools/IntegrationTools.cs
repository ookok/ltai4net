using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LTAI.AI;

namespace LTAI.Agent.Tools;

/// <summary>
/// GIS (4 providers: 高德/腾讯/百度/天地图), weather, translate, image search.
/// Env vars: AMAP_KEY, TENCENT_MAP_KEY, BAIDU_MAP_KEY, TIANDITU_KEY,
///           WEATHER_KEY, UNSPLASH_KEY, BAIDU_TRANSLATE_APPID/SECRET
/// </summary>
[ToolDomain("integration")]
public sealed class IntegrationTools
{
    private readonly IHttpClientFactory _httpF;
    public IntegrationTools(IHttpClientFactory h) => _httpF = h;
    private HttpClient H() => _httpF.CreateClient();
    private string? K(string n) => LTAI.Core.Configuration.SecretManager.Get(n)?.Trim();

    // ═══════════════════════════════════════════
    //  GIS — 4 providers
    // ═══════════════════════════════════════════

    [Description("地址转地理坐标(经纬度)。支持高德/腾讯/百度/天地图。\n"
        + "适用场景：搜索地址获取经纬度、地图标注、位置服务。\n"
        + "不适用场景：坐标转地址（请用 ReverseGeocode）、POI 搜索（请用 PoiSearch）。\n"
        + "关键参数：address — 地址文本；provider — 地图服务商。")]
    [ToolExample("北京市朝阳区国贸的经纬度是多少")]
    public async Task<string> Geocode(string address, string provider = "amap") => (provider.ToLowerInvariant() switch
    {
        "amap" or "高德" => await G($"https://restapi.amap.com/v3/geocode/geo?key={K("AMAP_KEY")}&address={E(address)}&output=JSON", "geocodes",
            r => $"📍 {r[0].GetProperty("formatted_address")}\n坐标: {r[0].GetProperty("location")}"),
        "tencent" or "腾讯" => await G($"https://apis.map.qq.com/ws/geocoder/v1/?key={K("TENCENT_MAP_KEY")}&address={E(address)}", "result",
            r => $"📍 {r.GetProperty("address")}\n坐标: {r.GetProperty("location").GetProperty("lng")},{r.GetProperty("location").GetProperty("lat")}"),
        "baidu" or "百度" => await G($"https://api.map.baidu.com/geocoding/v3/?ak={K("BAIDU_MAP_KEY")}&address={E(address)}&output=json", "result",
            r => $"📍 {r.GetProperty("level")}\n坐标(BD09): {r.GetProperty("location").GetProperty("lng")},{r.GetProperty("location").GetProperty("lat")}"),
        "tianditu" or "天地图" => await T($"https://api.tianditu.gov.cn/v2/geocoding?tk={K("TIANDITU_KEY")}", new { keyWord = address }, "location",
            r => $"📍 坐标: {r.GetProperty("lon")},{r.GetProperty("lat")}"),
        _ => $"Unknown provider. Use: amap, tencent, baidu, tianditu"
    }) ?? "Missing API key for " + provider;

    [Description("地理坐标(经纬度)转地址文本。支持高德/腾讯/百度/天地图。\n"
        + "适用场景：给定经纬度查询具体地址、位置反向解析。\n"
        + "不适用场景：地址转坐标（请用 Geocode）。\n"
        + "关键参数：lat/lng — 经纬度；provider — 地图服务商。")]
    [ToolExample("116.4,39.9 这个位置是哪里")]
    public async Task<string> ReverseGeocode(string location, string provider = "amap") => (provider.ToLowerInvariant() switch
    {
        "amap" or "高德" => await G($"https://restapi.amap.com/v3/geocode/regeo?key={K("AMAP_KEY")}&location={E(location)}&output=JSON", "regeocode",
            r => $"📍 {r.GetProperty("formatted_address")}"),
        "tencent" or "腾讯" => await G($"https://apis.map.qq.com/ws/geocoder/v1/?key={K("TENCENT_MAP_KEY")}&location={E(location)}", "result",
            r => $"📍 {r.GetProperty("address")}"),
        "baidu" or "百度" => await G($"https://api.map.baidu.com/reverse_geocoding/v3/?ak={K("BAIDU_MAP_KEY")}&location={E(location)}&output=json", "result",
            r => $"📍 {r.GetProperty("formatted_address")}"),
        "tianditu" or "天地图" => await T($"https://api.tianditu.gov.cn/v2/geocoding?tk={K("TIANDITU_KEY")}",
            new { lon = location.Split(',')[0], lat = location.Split(',')[1] }, "location",
            r => $"📍 {r.GetProperty("address")}"),
        _ => $"Unknown provider"
    }) ?? "Missing API key";

    [Description("搜索周边兴趣点(POI)：餐厅、商店、医院等。支持高德/腾讯/百度。\n"
        + "适用场景：查找附近的餐厅、找最近的地铁站、搜索周边设施。\n"
        + "不适用场景：地址转坐标（请用 Geocode）、坐标转地址（请用 ReverseGeocode）。\n"
        + "关键参数：keywords — 搜索关键词；city — 城市；provider — 地图服务商。")]
    [ToolExample("附近有什么好吃的")]
    [ToolExample("找一下附近的地铁站")]
    public async Task<string> PoiSearch(string keyword, string? city = null, int count = 10, string provider = "amap") => (provider.ToLowerInvariant() switch
    {
        "amap" or "高德" => await GA($"https://restapi.amap.com/v3/place/text?key={K("AMAP_KEY")}&keywords={E(keyword)}&offset={count}&output=JSON{(city != null ? $"&city={E(city)}" : "")}", "pois",
            r => $"- **{r.GetProperty("name")}**  地址: {GStr(r,"address")}  坐标: {r.GetProperty("location")}"),
        "tencent" or "腾讯" => await GA($"https://apis.map.qq.com/ws/place/v1/search?key={K("TENCENT_MAP_KEY")}&keyword={E(keyword)}&count={count}", "data",
            r => $"- **{r.GetProperty("title")}**  地址: {GStr(r,"address")}"),
        "baidu" or "百度" => await GA($"https://api.map.baidu.com/place/v2/search?ak={K("BAIDU_MAP_KEY")}&query={E(keyword)}&output=json&page_size={count}", "results",
            r => $"- **{r.GetProperty("name")}**  地址: {GStr(r,"address")}"),
        _ => $"Unknown provider"
    }) ?? "Missing API key";

    [Description("计算两个坐标点之间的驾车/步行/骑行距离。支持高德地图。\n"
        + "适用场景：计算两地驾车距离、估算出行时间和路程长度。\n"
        + "关键参数：origins — 起点坐标；destination — 终点坐标；type — 交通方式。")]
    [ToolExample("从天安门到王府井多远")]
    public async Task<string> DistanceCalc(string from, string to, int type = 1, string provider = "amap") => (provider.ToLowerInvariant() switch
    {
        "amap" or "高德" => await GA($"https://restapi.amap.com/v3/distance?key={K("AMAP_KEY")}&origins={E(from)}&destination={E(to)}&type={type}&output=JSON", "results",
            r => { var d = double.Parse(r.GetProperty("distance").GetString() ?? "0"); return $"距离: {d / 1000:F1} km"; }),
        _ => $"Only amap supports distance"
    }) ?? "Missing API key";

    [Description("根据 IP 地址查询地理位置。优先高德 API，备用 ip-api(无需 key)。\n"
        + "适用场景：查询某个 IP 的大致城市位置、网络请求来源定位。\n"
        + "关键参数：ip — IP 地址，为空则查询本机 IP。")]
    [ToolExample("查一下这个 IP 在哪")]
    public async Task<string> IpLocation(string? ip = null)
    {
        if (!string.IsNullOrEmpty(K("AMAP_KEY")))
            try { return await G($"https://restapi.amap.com/v3/ip?key={K("AMAP_KEY")}&output=JSON{(ip != null ? $"&ip={E(ip)}" : "")}", "province", r => $"🌐 {r}") ?? "No location data"; } catch { /* AMAP not configured — fallback */ }
        try { var j = await H().GetFromJsonAsync<JsonElement>(ip != null ? $"http://ip-api.com/json/{ip}" : "http://ip-api.com/json"); return $"🌐 {GStr(j,"city")}, {GStr(j,"regionName")}, {GStr(j,"country")}"; } catch (Exception ex) { return $"IP error: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════
    //  Weather — 和风天气
    // ═══════════════════════════════════════════

    [Description("查询指定城市天气情况。需要配置 WEATHER_KEY 环境变量。\n"
        + "适用场景：查询今天/明天天气、查看气温和降水概率、出行前查天气。\n"
        + "不适用场景：历史天气查询、空气质量查询（请用 ClassifyAirQuality）。\n"
        + "关键参数：city — 城市名。")]
    [ToolExample("北京明天天气怎么样")]
    [ToolExample("上海今天多少度")]
    public async Task<string> Weather(string city)
    {
        var key = K("WEATHER_KEY") ?? K("HEFENG_KEY");
        if (key == null) return "Set WEATHER_KEY env var";
        try
        {
            var h = H();
            var cj = await h.GetFromJsonAsync<JsonElement>($"https://geoapi.qweather.com/v2/city/lookup?key={key}&location={E(city)}");
            var loc = cj.GetProperty("location")[0].GetProperty("id").GetString() ?? "";
            var wj = await h.GetFromJsonAsync<JsonElement>($"https://devapi.qweather.com/v7/weather/now?key={key}&location={loc}");
            var n = wj.GetProperty("now");
            var sb = new StringBuilder($"🌤️ {city} {GStr(n,"text")} {GStr(n,"temp")}°C (体感 {GStr(n,"feelsLike")}°C)");
            if (n.TryGetProperty("windDir", out var wd)) sb.Append($" | {wd} {GStr(n,"windScale")}级");
            if (n.TryGetProperty("humidity", out var hu)) sb.Append($" | 湿度 {hu}%");
            try { var fj = await h.GetFromJsonAsync<JsonElement>($"https://devapi.qweather.com/v7/weather/24h?key={key}&location={loc}");
                sb.Append("\n未来几小时:"); foreach (var hh in fj.GetProperty("hourly").EnumerateArray().Take(4))
                    sb.Append($" {GStr(hh,"fxTime")[11..16]} {GStr(hh,"temp")}°C {GStr(hh,"text")}"); } catch { /* hourly forecast not available */ }
            return sb.ToString();
        }
        catch (Exception ex) { return $"Weather error: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════
    //  Translate — 百度翻译
    // ═══════════════════════════════════════════

    [Description("翻译文本到目标语言。需要配置百度翻译 API 密钥。\n"
        + "适用场景：翻译一段文字到中文或英文、理解外语内容。\n"
        + "关键参数：text — 待翻译文本；to — 目标语言(zh/en/ja等)。")]
    [ToolExample("把这段英文翻译成中文")]
    [ToolExample("用英语怎么说")]
    public async Task<string> Translate(string text, string to = "en", string from = "auto")
    {
        var appId = K("BAIDU_TRANSLATE_APPID");
        var secret = K("BAIDU_TRANSLATE_SECRET");
        if (appId == null || secret == null) return "Set BAIDU_TRANSLATE_APPID and BAIDU_TRANSLATE_SECRET";
        try
        {
            var salt = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var sign = MD5($"{appId}{text}{salt}{secret}");
            var j = await H().GetFromJsonAsync<JsonElement>($"https://fanyi-api.baidu.com/api/trans/vip/translate?q={E(text)}&from={from}&to={to}&appid={appId}&salt={salt}&sign={sign}");
            if (j.TryGetProperty("error_code", out var ec) && ec.GetString() != "0")
                return $"Error: {j.GetProperty("error_msg")}";
            return "## 翻译\n" + string.Join("\n", j.GetProperty("trans_result").EnumerateArray().Select(r => $"- {r.GetProperty("dst")}"));
        }
        catch (Exception ex) { return $"Translate error: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════
    //  Image Search — Unsplash
    // ═══════════════════════════════════════════

    [Description("按关键词搜索图片。需要配置 UNSPLASH_KEY 环境变量。\n"
        + "适用场景：找某个主题的图片、获取配图素材。\n"
        + "不适用场景：下载文件（请用 DownloadFile）、网页搜索（请用 WebSearch）。\n"
        + "关键参数：query — 搜索关键词；count — 返回数量。")]
    [ToolExample("找一些日落的图片")]
    public async Task<string> ImageSearch(string query, int count = 5)
    {
        var key = K("UNSPLASH_KEY");
        if (key == null) return "Set UNSPLASH_KEY env var";
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.unsplash.com/search/photos?query={E(query)}&per_page={Math.Clamp(count,1,10)}");
            req.Headers.Add("Authorization", $"Client-ID {key}");
            using var imgResp = await H().SendAsync(req);
            var j = await imgResp.Content.ReadFromJsonAsync<JsonElement>();
            var sb = new StringBuilder($"## Image: {query}\n");
            foreach (var r in j.GetProperty("results").EnumerateArray())
            {
                var c = GStr(r,"description") ?? GStr(r,"alt_description") ?? "";
                sb.AppendLine($"- ![{c}]({r.GetProperty("urls").GetProperty("thumb")}) by {r.GetProperty("user").GetProperty("name")}");
            }
            return sb.ToString();
        }
        catch (Exception ex) { return $"Image error: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════

    private static string E(string s) => Uri.EscapeDataString(s);
    private static string GStr(JsonElement j, string k) => j.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";

    private async Task<string?> G(string url, string dataPath, Func<JsonElement, string> fmt)
    {
        if (url.Contains("null")) return null;
        try
        {
            var j = await H().GetFromJsonAsync<JsonElement>(url);
            return j.TryGetProperty(dataPath, out var d) ? fmt(d) : $"API error: {GStr(j,"info")}";
        }
        catch (Exception ex)
        {
            // Sanitize: strip API keys from any leaked URL in exception messages
            var safe = SanitizeUrl(ex.Message);
            return $"API request failed: {safe}";
        }
    }

    private static string SanitizeUrl(string msg)
    {
        // Redact common API key patterns in URLs
        return System.Text.RegularExpressions.Regex.Replace(msg,
            @"(key|ak|tk|appid|secret|token)=[^&\s""']+",
            "$1=***REDACTED***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private async Task<string?> GA(string url, string arrPath, Func<JsonElement, string> fmt)
    {
        if (url.Contains("null")) return null;
        try
        {
            var j = await H().GetFromJsonAsync<JsonElement>(url);
            if (!j.TryGetProperty(arrPath, out var arr)) return $"API error: {GStr(j,"info")}";
            return "## Results\n" + string.Join("\n", arr.EnumerateArray().Select(fmt));
        }
        catch (Exception ex) { return $"API request failed: {SanitizeUrl(ex.Message)}"; }
    }

    private async Task<string?> T(string url, object body, string resPath, Func<JsonElement, string> fmt)
    {
        if (url.Contains("null")) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = JsonContent.Create(body);
            using var tResp = await H().SendAsync(req);
            var j = await tResp.Content.ReadFromJsonAsync<JsonElement>();
            return j.TryGetProperty(resPath, out var d) ? fmt(d) : $"API error: {GStr(j,"msg")}";
        }
        catch (Exception ex) { return $"API request failed: {SanitizeUrl(ex.Message)}"; }
    }

    private static string MD5(string s) { using var m = System.Security.Cryptography.MD5.Create(); return Convert.ToHexString(m.ComputeHash(Encoding.UTF8.GetBytes(s))).ToLowerInvariant(); }
}
