using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace LTAI.Agent.Tools;

/// <summary>
/// GIS (4 providers: 高德/腾讯/百度/天地图), weather, translate, image search.
/// Env vars: AMAP_KEY, TENCENT_MAP_KEY, BAIDU_MAP_KEY, TIANDITU_KEY,
///           WEATHER_KEY, UNSPLASH_KEY, BAIDU_TRANSLATE_APPID/SECRET
/// </summary>
public sealed class IntegrationTools
{
    private readonly IHttpClientFactory _httpF;
    public IntegrationTools(IHttpClientFactory h) => _httpF = h;
    private HttpClient H() => _httpF.CreateClient();
    private string? K(string n) => Environment.GetEnvironmentVariable(n)?.Trim();

    // ═══════════════════════════════════════════
    //  GIS — 4 providers
    // ═══════════════════════════════════════════

    [Description("地址转坐标。provider=amap|tencent|baidu|tianditu")]
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

    [Description("坐标转地址。provider=amap|tencent|baidu|tianditu")]
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

    [Description("POI搜索。provider=amap|tencent|baidu")]
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

    [Description("距离计算。provider=amap")]
    public async Task<string> DistanceCalc(string from, string to, int type = 1, string provider = "amap") => (provider.ToLowerInvariant() switch
    {
        "amap" or "高德" => await GA($"https://restapi.amap.com/v3/distance?key={K("AMAP_KEY")}&origins={E(from)}&destination={E(to)}&type={type}&output=JSON", "results",
            r => { var d = double.Parse(r.GetProperty("distance").GetString() ?? "0"); return $"距离: {d / 1000:F1} km"; }),
        _ => $"Only amap supports distance"
    }) ?? "Missing API key";

    [Description("IP定位。优先高德，备用ip-api(免key)")]
    public async Task<string> IpLocation(string? ip = null)
    {
        if (!string.IsNullOrEmpty(K("AMAP_KEY")))
            try { return await G($"https://restapi.amap.com/v3/ip?key={K("AMAP_KEY")}&output=JSON{(ip != null ? $"&ip={E(ip)}" : "")}", "province", r => $"🌐 {r}"); } catch { }
        try { var j = await H().GetFromJsonAsync<JsonElement>(ip != null ? $"http://ip-api.com/json/{ip}" : "http://ip-api.com/json"); return $"🌐 {GStr(j,"city")}, {GStr(j,"regionName")}, {GStr(j,"country")}"; } catch (Exception ex) { return $"IP error: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════
    //  Weather — 和风天气
    // ═══════════════════════════════════════════

    [Description("查询天气。需要 WEATHER_KEY 环境变量")]
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
                    sb.Append($" {GStr(hh,"fxTime")[11..16]} {GStr(hh,"temp")}°C {GStr(hh,"text")}"); } catch { }
            return sb.ToString();
        }
        catch (Exception ex) { return $"Weather error: {ex.Message}"; }
    }

    // ═══════════════════════════════════════════
    //  Translate — 百度翻译
    // ═══════════════════════════════════════════

    [Description("翻译文本。需要 BAIDU_TRANSLATE_APPID + BAIDU_TRANSLATE_SECRET")]
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

    [Description("搜索图片。需要 UNSPLASH_KEY")]
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
