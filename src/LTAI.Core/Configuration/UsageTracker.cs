using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LTAI.Core.Configuration;

/// <summary>
/// Default implementation of <see cref="IUsageTracker"/>.
/// Thread-safe via Interlocked and lock. Uses per-provider pricing from <see cref="KnownKeys.All"/>.
/// Supports optional scoped tracking (see <see cref="BeginScope"/>) for per-request cost attribution.
/// <b>Consumers:</b> Cli/Program.cs (dashboard), DashboardView, SessionStatsPanel,
/// TuiApp (status bar), CoreTests, MultiProviderChatClient (Record calls).
/// 
/// Backward-compat static forwarding: all static members delegate to <see cref="Default"/>,
/// so existing callers like <c>UsageTracker.Record(...)</c> continue to work unchanged.
/// New code should inject <see cref="IUsageTracker"/> via DI.
/// </summary>
public sealed class UsageTracker : IUsageTracker
{
    /// <summary>Per-model pricing overrides (¥/1M tokens) for cost calculation. Keyed by model ID.</summary>
    internal static readonly Dictionary<string, (decimal PriceIn, decimal PriceOut, decimal PriceInCache)> PerModelPricing = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deepseek-v4-flash"] = (1.0m, 2.0m, 0.02m),
        ["deepseek-v4-pro"] = (3.0m, 6.0m, 0.025m),
        ["deepseek-reasoner"] = (1.0m, 2.0m, 0.02m),
        ["deepseek-v3"] = (1.0m, 2.0m, 0.02m),
    };

    /// <summary>Global default instance. All static methods forward here.</summary>
    public static readonly UsageTracker Default = new();

    /// <summary>Current scoped tracker (if set via DI), or <see cref="Default"/>.</summary>
    internal static readonly AsyncLocal<UsageTracker?> Scoped = new();

    internal static UsageTracker Current => Scoped.Value ?? Default;

    // ── Instance methods ──

    public UsageTracker() { }

    /// <summary>
    /// Begin a scoped cost tracking session. Records token/cost deltas on dispose.
    /// Useful for per-request or per-conversation cost attribution in multi-tenant scenarios.
    /// Nested scopes are supported — each records from its start snapshot.
    /// Example:
    /// <code>
    /// using (var scope = UsageTracker.BeginScope())
    /// {
    ///     await llm.GetResponseAsync(...);
    ///     Console.WriteLine($"This request cost {scope.Cost:F4}¥");
    /// }
    /// </code>
    /// </summary>
    public UsageScope BeginScope() => new(
        Interlocked.Read(ref _promptTokens),
        Interlocked.Read(ref _completionTokens),
        _totalCost);

    /// <summary>Static forwarding to <see cref="Default"/>.</summary>
    public static UsageScope BeginScopeStatic() => Default.BeginScope();

    /// <summary>
    /// Records token/cost deltas within a scope. Created by <see cref="BeginScope"/>.
    /// Dispose to record the scope's contribution to the aggregate.
    /// </summary>
    public sealed class UsageScope : IDisposable
    {
        private readonly long _startPrompt;
        private readonly long _startCompletion;
        private readonly double _startCost;
        internal UsageScope(long startPrompt, long startCompletion, double startCost)
        {
            _startPrompt = startPrompt;
            _startCompletion = startCompletion;
            _startCost = startCost;
        }

        /// <summary>Prompt tokens used within this scope.</summary>
        public long PromptDelta => Interlocked.Read(ref _promptTokens) - _startPrompt;
        /// <summary>Completion tokens used within this scope.</summary>
        public long CompletionDelta => Interlocked.Read(ref _completionTokens) - _startCompletion;
        /// <summary>Estimated cost (¥) within this scope.</summary>
        public decimal Cost => (decimal)(Volatile.Read(ref _totalCost) - _startCost);

        public void Dispose() { }
    }

    // ══ Shared state (static, shared by Default + any scoped instances) ══
    private static long _promptTokens;
    private static long _completionTokens;
    private static double _totalCost;
    private static readonly object _costLock = new();
    private static long _requests;
    private static readonly Stopwatch _timer = Stopwatch.StartNew();
    private static string _activeModel = "";
    private static long _cacheHits;
    private static long _cacheMisses;
    private static long _cacheHitTokens;
    private static long _cacheMissTokens;
    private static long _toolCalls;
    private static string _currentTool = "";
    private static long _lastToolCallMs;
    private static long _lastLlmCallMs;
    private static long _lastStreamTokens;
    private static long _lastStreamElapsedMs;
    private static int _contextWindowSize = 64000;
    private static double _balance;
    private static string _balanceCurrency = "";
    private static string _balanceSource = "";
    private static readonly HttpClient _balanceHttp = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly object _balanceLock = new();
    private static readonly ConcurrentDictionary<string, int> _modelContextCache = new(StringComparer.OrdinalIgnoreCase);

    // ══ IUsageTracker explicit implementation (accessible when cast to interface) ══
    void IUsageTracker.Record(int prompt, int completion, string model) => RecordInternal(prompt, completion, model);
    void IUsageTracker.RecordWithCache(int prompt, int completion, int cacheHit, int cacheMiss, string model) => RecordInternal(prompt, completion, cacheHit, cacheMiss, model);

    // Last-matched model cache to avoid repeated linear scans of KnownKeys.All
    private static string? _lastLookupModel;
    private static KnownKeys.KeyInfo? _lastLookupKey;
    private static readonly object _lookupLock = new();

    private static KnownKeys.KeyInfo? LookupModel(string model)
    {
        if (string.IsNullOrEmpty(model)) return null;
        if (PerModelPricing.TryGetValue(model, out var pm))
        {
            return new KnownKeys.KeyInfo(
                "", "PerModel", "", null, null, model, pm.PriceIn, pm.PriceOut, pm.PriceInCache);
        }
        var cached = Volatile.Read(ref _lastLookupModel);
        if (string.Equals(cached, model, StringComparison.OrdinalIgnoreCase))
            return _lastLookupKey;
        lock (_lookupLock)
        {
            if (string.Equals(_lastLookupModel, model, StringComparison.OrdinalIgnoreCase))
                return _lastLookupKey;
            _lastLookupModel = model;
            _lastLookupKey = KnownKeys.All.FirstOrDefault(k =>
                !string.IsNullOrEmpty(k.Model) && model.StartsWith(k.Model, StringComparison.OrdinalIgnoreCase));
            return _lastLookupKey;
        }
    }

    private static void RecordInternal(int prompt, int completion, string model)
        => RecordInternal(prompt, completion, 0, prompt, model);

    private static void RecordInternal(int prompt, int completion, int cacheHit, int cacheMiss, string model)
    {
        Interlocked.Add(ref _promptTokens, prompt);
        Interlocked.Add(ref _completionTokens, completion);
        Interlocked.Add(ref _cacheHitTokens, cacheHit);
        Interlocked.Add(ref _cacheMissTokens, cacheMiss);
        Interlocked.Increment(ref _requests);
        if (!string.IsNullOrEmpty(model)) Interlocked.Exchange(ref _activeModel, model);

        var key = LookupModel(model);
        double cost;
        if (key != null && (key.PriceInPerM > 0 || key.PriceOutPerM > 0))
        {
            var cachePrice = key.PriceInCachePerM > 0 ? (double)key.PriceInCachePerM : (double)key.PriceInPerM;
            cost = (cacheHit / 1_000_000.0) * cachePrice
                 + (cacheMiss / 1_000_000.0) * (double)key.PriceInPerM
                 + (completion / 1_000_000.0) * (double)key.PriceOutPerM;
        }
        else
        {
            cost = (prompt / 1_000_000.0) * 1.0
                 + (completion / 1_000_000.0) * 4.0;
        }
        lock (_costLock) { _totalCost += cost; }
    }

    public static void Record(int prompt, int completion, string model = "") => RecordInternal(prompt, completion, model);
    public static void RecordWithCache(int prompt, int completion, int cacheHit, int cacheMiss, string model)
        => RecordInternal(prompt, completion, cacheHit, cacheMiss, model);

    long IUsageTracker.PromptTokens => Interlocked.Read(ref _promptTokens);
    long IUsageTracker.CompletionTokens => Interlocked.Read(ref _completionTokens);
    long IUsageTracker.Requests => Interlocked.Read(ref _requests);
    decimal IUsageTracker.EstimatedCost { get { lock (_costLock) { return (decimal)_totalCost; } } }
    string IUsageTracker.CostDisplay => $"¥{((IUsageTracker)this).EstimatedCost:F4}";
    string IUsageTracker.ActiveModel => !string.IsNullOrEmpty(_activeModel) ? _activeModel : "deepseek-v4-flash";
    void IUsageTracker.SetActiveModel(string model) => Interlocked.Exchange(ref _activeModel, model);
    void IUsageTracker.RecordCacheHit() => Interlocked.Increment(ref _cacheHits);
    void IUsageTracker.RecordCacheMiss() => Interlocked.Increment(ref _cacheMisses);
    long IUsageTracker.CacheHits => Interlocked.Read(ref _cacheHits);
    long IUsageTracker.CacheMisses => Interlocked.Read(ref _cacheMisses);
    double IUsageTracker.CacheHitRate
    {
        get
        {
            var hitT = Interlocked.Read(ref _cacheHitTokens);
            var missT = Interlocked.Read(ref _cacheMissTokens);
            if (hitT + missT > 0)
                return (double)hitT / (hitT + missT) * 100;
            var hitC = Interlocked.Read(ref _cacheHits);
            var missC = Interlocked.Read(ref _cacheMisses);
            return hitC + missC > 0 ? (double)hitC / (hitC + missC) * 100 : 0;
        }
    }
    double IUsageTracker.ContextRatio(int ovr) => CalcContextRatio(ovr);
    string IUsageTracker.ContextText(int ovr) => CalcContextText(ovr);
    string IUsageTracker.BalanceDisplay => BalanceDisplayStatic;
    void IUsageTracker.SetContextWindowSize(int size) => _contextWindowSize = size;
    async Task IUsageTracker.FetchBalanceAsync(string p, string? k) => await FetchBalanceStaticAsync(p, k);
    string IUsageTracker.Summary() => BuildSummary();
    long IUsageTracker.CacheHitTokens => Interlocked.Read(ref _cacheHitTokens);
    long IUsageTracker.CacheMissTokens => Interlocked.Read(ref _cacheMissTokens);
    long IUsageTracker.ToolCalls => Interlocked.Read(ref _toolCalls);
    string IUsageTracker.CacheSavedDisplay => CacheSavedDisplay;
    void IUsageTracker.RecordStreamingMetrics(long completionTokens, long elapsedMs)
    {
        Interlocked.Exchange(ref _lastStreamTokens, completionTokens);
        Interlocked.Exchange(ref _lastStreamElapsedMs, elapsedMs);
    }
    double? IUsageTracker.CurrentTps => CurrentTps;
    string IUsageTracker.TpsDisplay => TpsDisplay;
    void IUsageTracker.SetActiveTool(string toolName) => Interlocked.Exchange(ref _currentTool, toolName);
    string IUsageTracker.CurrentTool => _currentTool;

    // ══ Public static members (same names as before — backward compatible) ══
    public static long PromptTokens => Interlocked.Read(ref _promptTokens);
    public static long CompletionTokens => Interlocked.Read(ref _completionTokens);
    public static long TotalTokens => PromptTokens + CompletionTokens;
    public static long Requests => Interlocked.Read(ref _requests);
    public static TimeSpan Uptime => _timer.Elapsed;
    public static decimal EstimatedCost { get { lock (_costLock) { return (decimal)_totalCost; } } }
    public static string CostDisplay => $"¥{EstimatedCost:F4}";
    public static string ActiveModel => !string.IsNullOrEmpty(_activeModel) ? _activeModel : "deepseek-v4-flash";
    public static void SetActiveModel(string model) => Interlocked.Exchange(ref _activeModel, model);
    public static void RecordCacheHit() => Interlocked.Increment(ref _cacheHits);
    public static void RecordCacheMiss() => Interlocked.Increment(ref _cacheMisses);
    public static long CacheHits => Interlocked.Read(ref _cacheHits);
    public static long CacheMisses => Interlocked.Read(ref _cacheMisses);
    public static void RecordCacheTokens(long hitTokens, long missTokens)
    {
        Interlocked.Add(ref _cacheHitTokens, hitTokens);
        Interlocked.Add(ref _cacheMissTokens, missTokens);
    }
    public static long CacheHitTokens => Interlocked.Read(ref _cacheHitTokens);
    public static long CacheMissTokens => Interlocked.Read(ref _cacheMissTokens);
    public static double CacheHitRate
    {
        get
        {
            var hitT = Interlocked.Read(ref _cacheHitTokens);
            var missT = Interlocked.Read(ref _cacheMissTokens);
            if (hitT + missT > 0)
                return (double)hitT / (hitT + missT) * 100;
            var hitC = Interlocked.Read(ref _cacheHits);
            var missC = Interlocked.Read(ref _cacheMisses);
            return hitC + missC > 0 ? (double)hitC / (hitC + missC) * 100 : 0;
        }
    }
    public static string CacheSavedDisplay
    {
        get
        {
            var hitT = Interlocked.Read(ref _cacheHitTokens);
            if (hitT == 0) return "¥0.0000";
            var saved = hitT / 1_000_000.0 * (1.0 - 0.02);
            return $"¥{saved:F4}";
        }
    }
    public static void RecordToolCall() => Interlocked.Increment(ref _toolCalls);
    public static long ToolCalls => Interlocked.Read(ref _toolCalls);
    public static void SetActiveTool(string toolName) => Interlocked.Exchange(ref _currentTool, toolName);
    public static string CurrentTool => _currentTool ?? "";
    private static readonly AsyncLocal<Stopwatch?> _toolStopwatch = new();

    public static void StartToolTimer()
    {
        _toolStopwatch.Value = Stopwatch.StartNew();
    }
    public static void StopToolTimer()
    {
        var sw = _toolStopwatch.Value;
        if (sw != null)
        {
            sw.Stop();
            Interlocked.Exchange(ref _lastToolCallMs, sw.ElapsedMilliseconds);
            _toolStopwatch.Value = null;
        }
    }
    public static long ToolCallMs => Interlocked.Read(ref _lastToolCallMs);
    public static string ToolCallTimeDisplay
    {
        get
        {
            var ms = Interlocked.Read(ref _lastToolCallMs);
            return ms >= 1000 ? $"{ms / 1000.0:F1}s" : ms > 0 ? $"{ms}ms" : "";
        }
    }
    public static void RecordLlmCallDuration(long latencyMs)
    {
        Interlocked.Exchange(ref _lastLlmCallMs, latencyMs);
    }
    public static long LlmCallMs => Interlocked.Read(ref _lastLlmCallMs);
    public static string LlmCallTimeDisplay
    {
        get
        {
            var ms = Interlocked.Read(ref _lastLlmCallMs);
            return ms >= 1000 ? $"{ms / 1000.0:F1}s" : ms > 0 ? $"{ms}ms" : "";
        }
    }
    public static void RecordStreamingMetrics(long completionTokens, long elapsedMs)
    {
        Interlocked.Exchange(ref _lastStreamTokens, completionTokens);
        Interlocked.Exchange(ref _lastStreamElapsedMs, elapsedMs);
    }
    public static double? CurrentTps
    {
        get
        {
            var tok = Interlocked.Read(ref _lastStreamTokens);
            var ms = Interlocked.Read(ref _lastStreamElapsedMs);
            if (ms < 500 || tok < 4) return null;
            return Math.Round(tok * 1000.0 / ms, 1);
        }
    }
    public static string TpsDisplay
    {
        get
        {
            var tps = CurrentTps;
            return tps.HasValue ? $"{tps:F0} t/s" : "";
        }
    }
    public static void SetContextWindowSize(int size) => _contextWindowSize = size;
    public static double ContextRatio(int contextWindowOverride = 0) => CalcContextRatio(contextWindowOverride);
    public static string ContextText(int contextWindowOverride = 0) => CalcContextText(contextWindowOverride);
    public static string BalanceDisplay => BalanceDisplayStatic;
    public static async Task FetchBalanceAsync(string defaultProvider, string? apiKey = null)
        => await FetchBalanceStaticAsync(defaultProvider, apiKey);
    public static string Summary() => BuildSummary();

    // ══ Internal helpers (shared by static + interface impl) ══
    public static async Task RefreshModelInfoAsync(string endpoint, string apiKey)
    {
        try
        {
            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(apiKey)) return;
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/models");
            req.Headers.Authorization = new("Bearer", apiKey);
            using var resp = await _balanceHttp.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var json = JsonDocument.Parse(body);
            foreach (var model in json.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = model.GetProperty("id").GetString();
                if (id == null) continue;
                if (model.TryGetProperty("max_context_length", out var ctx))
                    _modelContextCache[id] = ctx.GetInt32();
                else if (model.TryGetProperty("context_length", out var ctx2))
                    _modelContextCache[id] = ctx2.GetInt32();
            }
        }
        catch { /* best-effort */ }
    }

    private static int EffectiveContextWindow(int ovr)
    {
        if (ovr > 0) return ovr;
        var modelKey = _activeModel;
        if (string.IsNullOrEmpty(modelKey))
            modelKey = "deepseek-v4-flash";
        var modelLimit = _modelContextCache.TryGetValue(modelKey, out var cached)
            ? cached
            : KnownContextWindows.TryGetValue(modelKey, out var known) ? known : 0;
        return Math.Max(modelLimit, _contextWindowSize);
    }
    private static double CalcContextRatio(int ovr)
    {
        var max = EffectiveContextWindow(ovr);
        if (max <= 0) return 0;
        return Math.Clamp((double)(PromptTokens % (max + 1)) / max, 0, 1);
    }
    private static string CalcContextText(int ovr)
    {
        var max = EffectiveContextWindow(ovr);
        if (max <= 0) return "";
        return $"{PromptTokens:N0}/{max:N0} ({(double)PromptTokens / max * 100:F1}%)";
    }
    private static string BalanceDisplayStatic
    {
        get
        {
            lock (_balanceLock)
            {
                return string.IsNullOrEmpty(_balanceSource) ? "N/A"
                    : $"{_balanceCurrency}{_balance:F2} ({_balanceSource})";
            }
        }
    }
    private static void SetBalance(double bal, string currency, string source)
    {
        lock (_balanceLock) { _balance = bal; _balanceCurrency = currency; _balanceSource = source; }
    }
    private static async Task FetchBalanceStaticAsync(string defaultProvider, string? apiKey)
    {
        try
        {
            if (string.IsNullOrEmpty(apiKey)) return;

            if (defaultProvider.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance");
                req.Headers.Authorization = new("Bearer", apiKey);
                using var resp = await _balanceHttp.SendAsync(req).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                if (root.TryGetProperty("balance_infos", out var infos) && infos.GetArrayLength() > 0)
                {
                    var info = infos[0];
                    var totalStr = info.GetProperty("total_balance").GetString() ?? "0";
                    var currency = info.GetProperty("currency").GetString() ?? "CNY";
                    var bal = double.Parse(totalStr, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture);
                    SetBalance(bal, currency == "CNY" ? "¥" : currency, "DeepSeek");
                }
                else
                {
                    var bal = root.TryGetProperty("balance", out var b) ? b.GetDouble() : 0;
                    SetBalance(bal, "¥", "DeepSeek");
                }
            }
            else if (defaultProvider.Contains("siliconflow", StringComparison.OrdinalIgnoreCase))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.siliconflow.cn/v1/user/balance");
                req.Headers.Authorization = new("Bearer", apiKey);
                using var resp = await _balanceHttp.SendAsync(req).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var json = JsonDocument.Parse(body);
                var bal = json.RootElement.GetProperty("balance").GetDouble();
                SetBalance(bal, "¥", "SiliconFlow");
            }
            else if (defaultProvider.Contains("openrouter", StringComparison.OrdinalIgnoreCase))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/auth/key");
                req.Headers.Authorization = new("Bearer", apiKey);
                using var resp = await _balanceHttp.SendAsync(req).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var json = JsonDocument.Parse(body);
                var credits = json.RootElement.GetProperty("data").GetProperty("credits").GetDouble();
                SetBalance(credits, "$", "OpenRouter");
            }
            else if (defaultProvider.Contains("zhipu", StringComparison.OrdinalIgnoreCase))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://open.bigmodel.cn/api/llm/balance");
                req.Headers.Authorization = new("Bearer", apiKey);
                using var resp = await _balanceHttp.SendAsync(req).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var json = JsonDocument.Parse(body);
                var bal = json.RootElement.GetProperty("data").GetProperty("total_balance").GetDouble();
                SetBalance(bal, "¥", "Zhipu(GLM)");
            }
            else if (defaultProvider.Contains("aliyun", StringComparison.OrdinalIgnoreCase) ||
                     defaultProvider.Contains("dashscope", StringComparison.OrdinalIgnoreCase))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    "https://dashscope.aliyuncs.com/api/v1/services/llm/balance");
                req.Headers.Authorization = new("Bearer", apiKey);
                using var resp = await _balanceHttp.SendAsync(req).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var json = JsonDocument.Parse(body);
                var bal = json.RootElement.GetProperty("available_balance").GetDouble();
                SetBalance(bal, "¥", "Aliyun(Qwen)");
            }
            else if (defaultProvider.Contains("moonshot", StringComparison.OrdinalIgnoreCase))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.moonshot.cn/v1/balance");
                req.Headers.Authorization = new("Bearer", apiKey);
                using var resp = await _balanceHttp.SendAsync(req).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var json = JsonDocument.Parse(body);
                var bal = json.RootElement.GetProperty("available_balance").GetDouble();
                SetBalance(bal, "¥", "Moonshot(Kimi)");
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>Known context windows for common models.</summary>
    private static readonly Dictionary<string, int> KnownContextWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deepseek-v4-flash"] = 1048576,
        ["deepseek-reasoner"] = 1048576,
        ["deepseek-v4-flash"] = 1048576,
        ["deepseek-v4-pro"] = 1048576,
        ["deepseek-v3"] = 65536,
        ["gpt-4o"] = 131072,
        ["gpt-4o-mini"] = 131072,
        ["gpt-4-turbo"] = 131072,
        ["gpt-4"] = 8192,
        ["gpt-3.5-turbo"] = 16384,
        ["qwen-plus"] = 131072,
        ["qwen-max"] = 32768,
        ["qwen-turbo"] = 131072,
        ["qwen-long"] = 1048576,
        ["glm-4-plus"] = 131072,
        ["glm-4"] = 131072,
        ["glm-4-flash"] = 131072,
        ["moonshot-v1-8k"] = 8192,
        ["moonshot-v1-32k"] = 32768,
        ["moonshot-v1-128k"] = 131072,
        ["claude-3-5-sonnet"] = 204800,
        ["claude-3-haiku"] = 204800,
        ["claude-3-opus"] = 204800,
        ["llama-3.3-70b"] = 131072,
        ["llama-3.1-8b"] = 131072,
        ["mixtral-8x7b"] = 32768,
        ["mistral-large"] = 131072,
        ["mistral-small"] = 32768,
        ["sonar-pro"] = 131072,
        ["sonar"] = 131072,
        ["grok-2"] = 131072,
        ["llama-v3p3"] = 131072,
    };
    private static string BuildSummary()
    {
        var p = PromptTokens;
        var c = CompletionTokens;
        return $"Tokens: {p:N0}+{c:N0}={TotalTokens:N0} | "
             + $"Requests: {Requests} | "
             + $"Cost: ¥{EstimatedCost:F4} | "
             + $"Uptime: {_timer.Elapsed:hh\\:mm\\:ss}";
    }
}
