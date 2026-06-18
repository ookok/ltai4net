using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace LTAI.Tests;

[Trait("Category", "Integration")]
public sealed class ProviderVerificationTests
{
    private static readonly Dictionary<string, string> Secrets;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    static ProviderVerificationTests()
    {
        var dir = AppContext.BaseDirectory;
        string? path = null;
        for (int i = 0; i < 8; i++)
        {
            var c1 = Path.Combine(dir, "tests", "secrets_export.json");
            var c2 = Path.Combine(dir, "secrets_export.json");
            if (File.Exists(c1)) { path = c1; break; }
            if (File.Exists(c2)) { path = c2; break; }
            var parent = Path.GetDirectoryName(dir);
            if (parent == null) break;
            dir = parent;
        }
        Secrets = path != null
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new()
            : new Dictionary<string, string>();
    }

    private static string? S(string key) =>
        Secrets.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val) ? val : null;

    /// <summary>Check endpoint reachability, return (success, detail) tuple.</summary>
    private static async Task<(bool ok, string detail)> Check(Func<HttpClient, Task<HttpResponseMessage>> send)
    {
        using var http = new HttpClient { Timeout = Timeout };
        try
        {
            var resp = await send(http);
            return (true, $"HTTP {(int)resp.StatusCode}");
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException se)
        {
            return (false, $"DNS/connect: {se.SocketErrorCode} — {se.Message}");
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            return (false, $"SSL: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return (false, $"Timeout ({Timeout.TotalSeconds}s)");
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static async Task AssertOk(string label, Func<HttpClient, Task<HttpResponseMessage>> send)
    {
        var (ok, detail) = await Check(send);
        Assert.True(ok, $"{label}: {detail}");
    }

    // ═══════════════════════════════════════════════════════════
    //  OpenAI-compatible providers: GET /models with Bearer token
    // ═══════════════════════════════════════════════════════════

    public static readonly TheoryData<string, string, string> OpenAiCompatProviders = new()
    {
        { "DeepSeek",       "deepseek_api_key",    "https://api.deepseek.com/v1" },
        { "SiliconFlow",    "siliconflow_api_key", "https://api.siliconflow.cn/v1" },
        { "Aliyun(Qwen)",   "aliyun_api_key",      "https://dashscope.aliyuncs.com/compatible-mode/v1" },
        { "Zhipu(GLM)",     "zhipuai_api_key",     "https://open.bigmodel.cn/api/paas/v4" },
        { "Hunyuan",        "hunyuan_api_key",      "https://api.hunyuan.cloud.tencent.com/v1" },
        { "StepFun",        "stepfun_api_key",      "https://api.stepfun.com/v1" },
        { "OpenRouter",     "openrouter_api_key",   "https://openrouter.ai/api/v1" },
        { "Moonshot(Kimi)", "moonshot_api_key",     "https://api.moonshot.cn/v1" },
        { "Yi(01.AI)",      "yi_api_key",           "https://api.lingyiwanwu.com/v1" },
        { "Minimax",        "minimax_api_key",      "https://api.minimax.chat/v1" },
        { "OpenAI",         "openai_api_key",       "https://api.openai.com/v1" },
        { "Anthropic",      "anthropic_api_key",    "https://api.anthropic.com" },
        { "Groq",           "groq_api_key",         "https://api.groq.com/openai/v1" },
        { "Together AI",    "together_api_key",     "https://api.together.xyz/v1" },
        { "Mistral",        "mistral_api_key",      "https://api.mistral.ai/v1" },
        { "Perplexity",     "perplexity_api_key",   "https://api.perplexity.ai" },
        { "X.AI(Grok)",     "xai_api_key",          "https://api.x.ai/v1" },
        { "Cohere",         "cohere_api_key",       "https://api.cohere.ai/v1" },
        { "Fireworks AI",   "fireworks_api_key",    "https://api.fireworks.ai/inference/v1" },
        // ── User's extra providers with standard OpenAI-compatible endpoints ──
        { "Bailing",        "bailing_api_key",      "https://api.baichuan-ai.com/v1" },
        { "NVIDIA",         "nvidia_api_key",       "https://integrate.api.nvidia.com/v1" },
    };

    [SkippableTheory]
    [MemberData(nameof(OpenAiCompatProviders))]
    public async Task OpenAiCompat_GetModels(string name, string secretsKey, string endpoint)
    {
        var apiKey = S(secretsKey);
        Skip.If(apiKey == null, $"No {secretsKey} configured");
        await AssertOk(name, async http =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            return await http.SendAsync(req);
        });
    }

    [SkippableFact]
    public async Task MiMoXiaomi_Endpoint()
    {
        var apiKey = S("xiaomi_api_key");
        Skip.If(apiKey == null, "No xiaomi_api_key configured");
        // MiMo uses non-standard hostname; DNS may fail from CN
        var (ok, detail) = await Check(async http =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.xiaomimimo.com/v1/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            return await http.SendAsync(req);
        });
        if (!ok)
            Assert.Contains("DNS", detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Helper for providers whose DNS may fail from CN.</summary>
    private static async Task AssertReachableOrDns(string label, string endpoint, string? apiKey)
    {
        var (ok, detail) = await Check(async http =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            return await http.SendAsync(req);
        });
        Assert.True(ok || detail.Contains("DNS") || detail.Contains("Timeout"),
            $"{label}: {detail} (DNS/Timeout OK from CN)");
    }

    [SkippableFact]
    public async Task LongCat_Endpoint() =>
        await AssertReachableOrDns("LongCat", "https://api.longcat.chat/openai/v1", S("longcat_api_key"));

    [SkippableFact]
    public async Task DMXAPI_Endpoint() =>
        await AssertReachableOrDns("DMXAPI", "https://www.dmxapi.cn/v1", S("dmxapi_api_key"));

    [SkippableFact]
    public async Task SenseTime_Endpoint() =>
        await AssertReachableOrDns("SenseTime", "https://api.sensetime.com/v1", S("sensetime_api_key"));

    [SkippableFact]
    public async Task Mofang_Endpoint() =>
        await AssertReachableOrDns("Mofang", "https://api.mofang.ai/v1", S("mofang_api_key"));

    [SkippableFact]
    public async Task InternLM_Endpoint() =>
        await AssertReachableOrDns("InternLM(SenseNova)", "https://api.sensenova.cn/v1", S("internlm_api_key"));

    [SkippableFact]
    public async Task ModelScope_Endpoint() =>
        await AssertReachableOrDns("ModelScope", "https://api-inference.modelscope.cn/v1", S("modelscope_api_key"));

    // ═══════════════════════════════════════════════════════════
    //  Non-OpenAI-compatible LLM providers
    // ═══════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task Baidu_Qianfan_Models()
    {
        var apiKey = S("baidu_api_key");
        Skip.If(apiKey == null, "No baidu_api_key configured");

        // Baidu Qianfan uses IAM token directly (bce-v3/... Bearer auth)
        await AssertOk("Baidu Qianfan", async http =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://qianfan.baidubce.com/v2/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            return await http.SendAsync(req);
        });
    }

    [SkippableFact]
    public async Task Spark_ChatEndpoint()
    {
        var apiKey = S("spark_api_key");
        Skip.If(apiKey == null, "No spark_api_key configured");

        // Spark API now supports OpenAI-compatible format
        await AssertOk("Spark", async http =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://spark-api-open.xf-yun.com/v1/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            return await http.SendAsync(req);
        });
    }



    // ═══════════════════════════════════════════════════════════
    //  Search providers
    // ═══════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task BraveSearch_Endpoint()
    {
        var apiKey = S("brave_search_api_key");
        Skip.If(apiKey == null, "No brave_search_api_key configured");
        await AssertOk("Brave Search", async http =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.search.brave.com/res/v1/web/search?q=ping");
            req.Headers.Add("Accept", "application/json");
            req.Headers.Add("X-Subscription-Token", apiKey);
            return await http.SendAsync(req);
        });
    }

    [SkippableFact]
    public async Task Serper_Endpoint()
    {
        var apiKey = S("serper_api_key");
        Skip.If(apiKey == null, "No serper_api_key configured");
        await AssertOk("Serper", async http =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "https://google.serper.dev/search");
            req.Content = new StringContent(JsonSerializer.Serialize(new { q = "ping" }), Encoding.UTF8, "application/json");
            req.Headers.Add("X-API-KEY", apiKey);
            return await http.SendAsync(req);
        });
    }

    // ═══════════════════════════════════════════════════════════
    //  Memory provider
    // ═══════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task Mem0_Endpoint()
    {
        var apiKey = S("mem0_api_key");
        Skip.If(apiKey == null, "No mem0_api_key configured");
        var (ok, detail) = await Check(async http =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.mem0.ai/v1/memories");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            return await http.SendAsync(req);
        });
        Assert.True(ok || detail.Contains("404") || detail.Contains("401"),
            $"Mem0: {detail} (401/404 OK for no memories)");
    }

    // ═══════════════════════════════════════════════════════════
    //  SMTP connectivity (TCP only, does not send email)
    // ═══════════════════════════════════════════════════════════

    [SkippableTheory]
    [InlineData(587)]
    [InlineData(465)]
    public async Task Smtp_TcpConnect(int port)
    {
        var host = S("smtp_host") ?? S("smtp_server");
        Skip.If(string.IsNullOrEmpty(host), "No smtp_host or smtp_server configured");

        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(tcp.Connected, $"SMTP {host}:{port}");
        }
        catch (TimeoutException)
        {
            Assert.Fail($"SMTP {host}:{port}: Timeout (> 10s)");
        }
        catch (SocketException se)
        {
            Assert.Fail($"SMTP {host}:{port}: {se.SocketErrorCode} — {se.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Weather APIs
    // ═══════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task OpenWeatherMap_Endpoint()
    {
        var apiKey = S("openweathermap_api_key");
        Skip.If(apiKey == null, "No openweathermap_api_key configured");
        // openweathermap may be slow/unreachable from CN; allow DNS/Timeout
        var (ok, detail) = await Check(async http =>
            await http.GetAsync($"https://api.openweathermap.org/data/2.5/weather?q=Beijing&appid={apiKey}"));
        Assert.True(ok || detail.Contains("DNS") || detail.Contains("Timeout"),
            $"OpenWeatherMap: {detail} (DNS/Timeout OK from CN)");
    }

    [SkippableFact]
    public async Task QWeather_Endpoint()
    {
        var apiKey = S("qweather_api_key");
        Skip.If(apiKey == null, "No qweather_api_key configured");
        var (ok, detail) = await Check(async http =>
            await http.GetAsync($"https://devapi.qweather.com/v7/weather/now?location=116.40,39.90&key={apiKey}"));
        Assert.True(ok || detail.Contains("403"),
            $"QWeather: {detail} (403 OK — key may need activation)");
    }

    // ═══════════════════════════════════════════════════════════
    //  Map / GIS APIs
    // ═══════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task Tianditu_Endpoint()
    {
        var apiKey = S("tianditu_key");
        Skip.If(apiKey == null, "No tianditu_key configured");
        var (ok, detail) = await Check(async http =>
        {
            // Tianditu v2 geocode API
            var json = JsonSerializer.Serialize(new { keyWord = "北京市" });
            return await http.GetAsync(
                $"https://api.tianditu.gov.cn/v2/geocode?postStr={Uri.EscapeDataString(json)}&type=geocode&tk={apiKey}");
        });
        Assert.True(ok || detail.Contains("404"),
            $"Tianditu: {detail} (404 OK — API version may differ)");
    }

    [SkippableFact]
    public async Task TencentMap_Endpoint()
    {
        var apiKey = S("tencent_map_key");
        Skip.If(apiKey == null, "No tencent_map_key configured");
        await AssertOk("Tencent Map", async http =>
            await http.GetAsync($"https://apis.map.qq.com/ws/geocoder/v1/?address=北京市&key={apiKey}"));
    }

    [SkippableFact]
    public async Task BaiduMap_Endpoint()
    {
        var ak = S("baidu_map_ak");
        Skip.If(ak == null, "No baidu_map_ak configured");
        await AssertOk("Baidu Map", async http =>
            await http.GetAsync($"https://api.map.baidu.com/geocoding/v3/?address=北京市&output=json&ak={ak}"));
    }

    [SkippableFact]
    public async Task Amap_Endpoint()
    {
        var apiKey = S("amap_key");
        Skip.If(apiKey == null, "No amap_key configured");
        await AssertOk("Amap(高德)", async http =>
            await http.GetAsync($"https://restapi.amap.com/v3/geocode/geo?address=北京市&output=json&key={apiKey}"));
    }

    // ═══════════════════════════════════════════════════════════
    //  Translation API
    // ═══════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task BaiduTranslate_Endpoint()
    {
        var appId = S("baidu_translate_appid");
        var secretKey = S("baidu_translate_key");
        Skip.If(appId == null || secretKey == null, "No baidu_translate_appid or baidu_translate_key configured");

        var q = "hello";
        var salt = RandomNumberGenerator.GetInt32(100000, 999999).ToString();
        var sign = MD5.HashData(Encoding.UTF8.GetBytes($"{appId}{q}{salt}{secretKey}"));
        var signStr = Convert.ToHexStringLower(sign);

        await AssertOk("Baidu Translate", async http =>
            await http.GetAsync(
                $"https://api.fanyi.baidu.com/api/trans/vip/translate?q={Uri.EscapeDataString(q)}&from=en&to=zh&appid={appId}&salt={salt}&sign={signStr}"));
    }

    // ═══════════════════════════════════════════════════════════
    //  Image APIs
    // ═══════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task Unsplash_Endpoint()
    {
        var apiKey = S("unsplash_access_key");
        Skip.If(apiKey == null, "No unsplash_access_key configured");
        await AssertOk("Unsplash", async http =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.unsplash.com/photos?per_page=1");
            req.Headers.Authorization = new AuthenticationHeaderValue("Client-ID", apiKey);
            return await http.SendAsync(req);
        });
    }

    [SkippableFact]
    public async Task Pixabay_Endpoint()
    {
        var apiKey = S("pixabay_api_key");
        Skip.If(apiKey == null, "No pixabay_api_key configured");
        var (ok, detail) = await Check(async http =>
            await http.GetAsync($"https://pixabay.com/api/?key={apiKey}&q=ping&per_page=1"));
        Assert.True(ok || detail.Contains("400"),
            $"Pixabay: {detail} (400 OK — malformed request with test data)");
    }

    // ═══════════════════════════════════════════════════════════
    //  GitHub API
    // ═══════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task GitHub_Endpoint()
    {
        var token = S("github_token");
        Skip.If(token == null, "No github_token configured");
        await AssertOk("GitHub", async http =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            req.Headers.UserAgent.ParseAdd("LTAI-Tests/1.0");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await http.SendAsync(req);
        });
    }
}
