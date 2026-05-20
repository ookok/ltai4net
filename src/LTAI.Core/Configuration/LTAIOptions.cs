using System.Text.Json.Serialization;

namespace LTAI.Core.Configuration;

public sealed class LTAIOptions
{
    public const string SectionName = "LTAI";

    public AIConfig AI { get; init; } = new();
    public WebConfig Web { get; init; } = new();
    public VectorConfig Vector { get; init; } = new();
    public NetworkConfig Network { get; init; } = new();

    [JsonPropertyName("data_directory")]
    public string DataDirectory { get; init; } = ".livingtree";

    [JsonPropertyName("integration_urls")]
    public IntegrationUrlsConfig IntegrationUrls { get; init; } = new();

    [JsonPropertyName("thresholds")]
    public ThresholdsConfig Thresholds { get; init; } = new();
}

public sealed class LayerConfig
{
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("temperature")]
    public float? Temperature { get; init; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Provider) && !string.IsNullOrWhiteSpace(Model);
}

public sealed class AIConfig
{
    [JsonPropertyName("default_provider")]
    public string DefaultProvider { get; init; } = "deepseek";

    [JsonPropertyName("l0")]
    public LayerConfig L0 { get; init; } = new()
    {
        Provider = "siliconflow",
        Model = "BAAI/bge-large-zh-v1.5"
    };

    [JsonPropertyName("l1")]
    public LayerConfig L1 { get; init; } = new()
    {
        Provider = "deepseek",
        Model = "deepseek-v4-flash",
        Temperature = 0.3f
    };

    [JsonPropertyName("l2")]
    public LayerConfig L2 { get; init; } = new()
    {
        Provider = "deepseek",
        Model = "deepseek-v4-pro",
        Temperature = 0.3f
    };

    [JsonPropertyName("fast_model")]
    public string FastModel => L1.Model;

    [JsonPropertyName("deep_model")]
    public string DeepModel => L2.Model;

    [JsonPropertyName("embedding_model")]
    public string EmbeddingModel => L0.Model;

    [JsonPropertyName("daily_budget_usd")]
    public decimal DailyBudgetUsd { get; init; } = 10.00m;

    [JsonPropertyName("default_temperature")]
    public float DefaultTemperature { get; init; } = 0.3f;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; init; } = 4096;

    [JsonPropertyName("timeout_ms")]
    public int TimeoutMs { get; init; } = 60000;

    [JsonPropertyName("max_collaboration_rounds")]
    public int MaxCollaborationRounds { get; init; } = 5;

    [JsonPropertyName("system_prompts")]
    public SystemPromptsConfig SystemPrompts { get; init; } = new();

    [JsonPropertyName("providers")]
    public Dictionary<string, ProviderConfig> Providers { get; init; } = new();

    [JsonPropertyName("provider_catalog")]
    public ProviderCatalog ProviderCatalog { get; init; } = new();

    public LayerConfig GetLayerConfig(string layer)
    {
        return layer.ToUpperInvariant() switch
        {
            "L0" => L0,
            "L1" => L1,
            "L2" => L2,
            "FAST" => L1,
            "DEEP" => L2,
            "EMBEDDING" => L0,
            _ => L2
        };
    }
}

public sealed class ProviderConfig
{
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; init; } = string.Empty;

    [JsonPropertyName("api_key")]
    public string ApiKey { get; init; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;
}

public sealed class WebConfig
{
    [JsonPropertyName("host")]
    public string Host { get; init; } = "0.0.0.0";

    [JsonPropertyName("port")]
    public int Port { get; init; } = 8080;

    [JsonPropertyName("rate_limit_per_minute")]
    public int RateLimitPerMinute { get; init; } = 60;
}

public sealed class VectorConfig
{
    [JsonPropertyName("dimension")]
    public int Dimension { get; init; } = 384;

    [JsonPropertyName("backend")]
    public string Backend { get; init; } = "hnsw";

    [JsonPropertyName("cache_size_mb")]
    public int CacheSizeMb { get; init; } = 256;
}

public sealed class NetworkConfig
{
    [JsonPropertyName("p2p_port")]
    public int P2PPort { get; init; } = 9090;

    [JsonPropertyName("discovery_endpoint")]
    public string DiscoveryEndpoint { get; init; } = string.Empty;
}

public sealed class ProviderCatalog
{
    [JsonPropertyName("entries")]
    public List<ProviderEntry> Entries { get; init; } = new();
}

public sealed class ProviderEntry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; init; } = string.Empty;

    [JsonPropertyName("default_model")]
    public string DefaultModel { get; init; } = string.Empty;

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; init; } = new();

    [JsonPropertyName("tier_variants")]
    public List<ProviderTierVariantEntry> TierVariants { get; init; } = new();
}

public sealed class ProviderTierVariantEntry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;
}

public sealed class SystemPromptsConfig
{
    [JsonPropertyName("investigate_before_answering")]
    public string InvestigateBeforeAnswering { get; init; } = """
        Never speculate about content you haven't read. If referencing specific files or data,
        you must read them first before answering. Before proposing solutions, always check
        relevant files and data. When answering knowledge questions, cite sources you actually
        retrieved, don't fabricate. Ensure answers are pragmatic, accurate, and hallucination-free.
        """;

    [JsonPropertyName("progressive_work_pattern")]
    public string ProgressiveWorkPattern { get; init; } = """
        This is a long task. Progress incrementally, focusing on a few items at a time.
        Track your progress. Don't exhaust context with large uncommitted work.
        Systematically continue until the task is complete. Before context window approaches
        limits, save current progress and state to memory/file.
        """;

    [JsonPropertyName("parallel_execution")]
    public string ParallelExecution { get; init; } = """
        If you plan to call multiple tools with no inter-dependencies, make all independent
        tool calls in parallel. Prioritize simultaneous calls when operations can be parallel
        rather than sequential. For example: read multiple files at once, make multiple
        search queries at once. Maximize parallelism for speed and efficiency.
        """;

    [JsonPropertyName("anti_overengineering")]
    public string AntiOverengineering { get; init; } = """
        Only make changes that are directly requested or clearly necessary. Keep solutions
        simple and focused. Don't add features, refactor code, or make "improvements" beyond
        requirements. Don't design for hypothetical future needs. The right complexity is
        the minimum needed for the current task.
        """;

    [JsonPropertyName("source_verification")]
    public string SourceVerification { get; init; } = """
        When gathering data, verify information across multiple sources. Develop competing
        hypotheses. Track confidence levels. Periodically self-critique methods and plans.
        Persist findings to files for transparency.
        """;
}

public sealed class IntegrationUrlsConfig
{
    [JsonPropertyName("weather_openweathermap")]
    public string WeatherOpenWeatherMap { get; init; } = "https://api.openweathermap.org/data/2.5/weather";

    [JsonPropertyName("weather_qweather_geo")]
    public string WeatherQWeatherGeo { get; init; } = "https://geoapi.qweather.com/v2/city/lookup";

    [JsonPropertyName("weather_qweather_now")]
    public string WeatherQWeatherNow { get; init; } = "https://devapi.qweather.com/v7/weather/now";

    [JsonPropertyName("translate_baidu")]
    public string TranslateBaidu { get; init; } = "https://fanyi-api.baidu.com/api/trans/vip/translate";

    [JsonPropertyName("sms_aliyun")]
    public string SmsAliYun { get; init; } = "https://dysmsapi.aliyuncs.com/";

    [JsonPropertyName("maps_baidu")]
    public string MapsBaidu { get; init; } = "https://api.map.baidu.com/";

    [JsonPropertyName("maps_amap")]
    public string MapsAmap { get; init; } = "https://restapi.amap.com/";

    [JsonPropertyName("maps_tencent")]
    public string MapsTencent { get; init; } = "https://apis.map.qq.com/";

    [JsonPropertyName("maps_tianditu")]
    public string MapsTianditu { get; init; } = "https://t{s}.tianditu.gov.cn/";

    [JsonPropertyName("imagesearch_unsplash")]
    public string ImageSearchUnsplash { get; init; } = "https://api.unsplash.com/search/photos";

    [JsonPropertyName("imagesearch_pixabay")]
    public string ImageSearchPixabay { get; init; } = "https://pixabay.com/api/";

    [JsonPropertyName("telegram_base")]
    public string TelegramBase { get; init; } = "https://api.telegram.org";

    [JsonPropertyName("ollama_url")]
    public string OllamaUrl { get; init; } = "http://localhost:11434";

    [JsonPropertyName("ffmpeg_path")]
    public string FfmpegPath { get; init; } = "tools/ffmpeg.exe";

    [JsonPropertyName("sandbox_remote_url")]
    public string SandboxRemoteUrl { get; init; } = "http://localhost:8000";
}

public sealed class ThresholdsConfig
{
    [JsonPropertyName("consolidation_learning_rate")]
    public double ConsolidationLearningRate { get; init; } = 0.015;

    [JsonPropertyName("consolidation_decay_rate")]
    public double ConsolidationDecayRate { get; init; } = 0.999;

    [JsonPropertyName("consolidation_interval_minutes")]
    public int ConsolidationIntervalMinutes { get; init; } = 2;

    [JsonPropertyName("grpo_learning_rate")]
    public double GrpoLearningRate { get; init; } = 0.02;

    [JsonPropertyName("evolution_population_size")]
    public int EvolutionPopulationSize { get; init; } = 32;

    [JsonPropertyName("evolution_mutation_rate")]
    public double EvolutionMutationRate { get; init; } = 0.3;

    [JsonPropertyName("retrieval_zero_result_warning_rate")]
    public double RetrievalZeroResultWarningRate { get; init; } = 0.15;

    [JsonPropertyName("retrieval_recall_drift_warning_percent")]
    public double RetrievalRecallDriftWarningPercent { get; init; } = 5.0;

    [JsonPropertyName("loafing_threshold")]
    public double LoafingThreshold { get; init; } = 0.35;

    [JsonPropertyName("sovereignty_gap_threshold")]
    public double SovereigntyGapThreshold { get; init; } = 0.25;

    [JsonPropertyName("purge_hot_threshold_sec")]
    public int PurgeHotThresholdSec { get; init; } = 3600;

    [JsonPropertyName("purge_warm_threshold_sec")]
    public int PurgeWarmThresholdSec { get; init; } = 86400;
}
