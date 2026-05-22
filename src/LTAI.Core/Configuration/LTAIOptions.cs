using System.Text.Json.Serialization;
using LTAI.Core.Network;

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

    [JsonPropertyName("model_pricing")]
    public ModelPricingConfig ModelPricing { get; init; } = new();

    [JsonPropertyName("social_load")]
    public SocialLoadFamilyConfig SocialLoad { get; init; } = new();

    [JsonPropertyName("stealth_browser")]
    public StealthBrowserConfig StealthBrowser { get; init; } = new();

    public ToolsAutoConfig ToolsAuto { get; init; } = new();

    public HttpAcceleratorConfig HttpAccelerator { get; init; } = new();

    [JsonPropertyName("economy")]
    public EconomyOptions? Economy { get; init; }
}

public sealed class EconomyOptions
{
    [JsonPropertyName("policy")]
    public string? Policy { get; init; }

    [JsonPropertyName("daily_budget_yuan")]
    public double DailyBudgetYuan { get; init; } = 50.0;

    [JsonPropertyName("max_task_budget_yuan")]
    public double MaxTaskBudgetYuan { get; init; } = 10.0;
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

    [JsonPropertyName("chat_completions_path")]
    public string ChatCompletionsPath { get; set; } = "/v1/chat/completions";

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

    [JsonPropertyName("queue_path")]
    public string QueuePath { get; init; } = ".livingtree/p2p/queue.db";
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

public sealed class ModelPricingConfig
{
    [JsonPropertyName("input_per_1m")]
    public Dictionary<string, double> InputPer1M { get; init; } = new()
    {
        ["gpt-4o"] = 2.50, ["gpt-4o-mini"] = 0.15,
        ["claude-sonnet"] = 3.00, ["claude-haiku"] = 0.25,
        ["deepseek-v3"] = 0.27, ["deepseek-r1"] = 0.55,
        ["deepseek-v4-pro"] = 3.0, ["deepseek-v4-flash"] = 1.0,
        ["qwen-max"] = 0.40, ["qwen-turbo"] = 0.08,
        ["qwen3.6-plus"] = 2.90, ["qwen3.6-flash"] = 0.73,
        ["qwen3.6-max"] = 8.70, ["qwq-plus"] = 5.80,
        ["qvq-max"] = 8.70, ["sensetime"] = 0.0,
        ["default"] = 0.50
    };

    [JsonPropertyName("output_per_1m")]
    public Dictionary<string, double> OutputPer1M { get; init; } = new()
    {
        ["gpt-4o"] = 10.00, ["gpt-4o-mini"] = 0.60,
        ["claude-sonnet"] = 15.00, ["claude-haiku"] = 1.25,
        ["deepseek-v3"] = 1.10, ["deepseek-r1"] = 2.19,
        ["deepseek-v4-pro"] = 6.0, ["deepseek-v4-flash"] = 2.0,
        ["qwen-max"] = 1.60, ["qwen-turbo"] = 0.32,
        ["qwen3.6-plus"] = 17.40, ["qwen3.6-flash"] = 2.90,
        ["qwen3.6-max"] = 43.50, ["qwq-plus"] = 11.60,
        ["qvq-max"] = 17.40, ["sensetime"] = 0.0,
        ["default"] = 2.00
    };

    [JsonPropertyName("degradation_chain")]
    public Dictionary<string, string> DegradationChain { get; init; } = new()
    {
        ["gpt-4o"] = "gpt-4o-mini",
        ["claude-sonnet"] = "claude-haiku",
        ["deepseek-r1"] = "deepseek-v3",
        ["deepseek-v4-pro"] = "deepseek-v4-flash",
        ["qwen-max"] = "qwen-turbo",
        ["qwen3.6-max"] = "qwen3.6-flash"
    };
}

public sealed class SocialLoadFamilyConfig
{
    [JsonPropertyName("resilience")]
    public Dictionary<string, double> Resilience { get; init; } = new()
    {
        ["claude"] = 5.0,
        ["gemini"] = 1.5,
        ["gpt"] = 1.0,
        ["qwen"] = 1.2,
        ["default"] = 1.0
    };

    [JsonPropertyName("base_authority")]
    public Dictionary<string, double> BaseAuthority { get; init; } = new()
    {
        ["claude"] = 0.9,
        ["gemini"] = 0.75,
        ["gpt"] = 0.7,
        ["qwen"] = 0.65,
        ["default"] = 0.5
    };
}

public sealed class StealthBrowserConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("engine")]
    public string Engine { get; init; } = "cloakbrowser";

    [JsonPropertyName("executable_path")]
    public string ExecutablePath { get; init; } = "";

    [JsonPropertyName("auto_download")]
    public bool AutoDownload { get; init; } = true;

    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; init; } = "https://github.com/CloakHQ/CloakBrowser/releases/latest/download";

    [JsonPropertyName("download_mirror")]
    public string DownloadMirror { get; init; } = "https://gitee.com/mirrors/cloakbrowser/releases/latest/download";

    [JsonPropertyName("cache_dir")]
    public string CacheDir { get; init; } = ".livingtree/browser/stealth";

    [JsonPropertyName("docker_image")]
    public string DockerImage { get; init; } = "cloakhq/cloakbrowser:latest";

    [JsonPropertyName("cdp_port")]
    public int CdpPort { get; init; } = 9222;

    [JsonPropertyName("headless")]
    public bool Headless { get; init; } = true;

    [JsonPropertyName("humanize")]
    public bool Humanize { get; init; } = false;

    [JsonPropertyName("proxy")]
    public string? Proxy { get; init; }

    [JsonPropertyName("extra_args")]
    public List<string> ExtraArgs { get; init; } = new();

    [JsonPropertyName("launch_timeout_ms")]
    public int LaunchTimeoutMs { get; init; } = 30000;

    [JsonPropertyName("user_agent")]
    public string UserAgent { get; init; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36";

    [JsonPropertyName("block_trackers")]
    public bool BlockTrackers { get; init; } = true;

    [JsonPropertyName("randomize_viewport")]
    public bool RandomizeViewport { get; init; } = true;

    [JsonPropertyName("inject_stealth_scripts")]
    public bool InjectStealthScripts { get; init; } = true;
}

public sealed class ToolsAutoConfig
{
    [JsonPropertyName("auto_download")]
    public bool AutoDownload { get; init; } = true;

    [JsonPropertyName("download_dir")]
    public string DownloadDir { get; init; } = ".livingtree/tools";

    [JsonPropertyName("wsl2")]
    public ToolAutoItem Wsl2 { get; init; } = new()
    {
        Enabled = true,
        Name = "WSL2",
        InstallUrl = "https://aka.ms/wslstorepage",
        AutoInstallDistro = "Ubuntu-24.04"
    };

    [JsonPropertyName("python")]
    public ToolAutoItem Python { get; init; } = new()
    {
        Enabled = true,
        Name = "Python",
        InstallUrl = "https://www.python.org/downloads/",
        MinVersion = "3.10"
    };

    [JsonPropertyName("nodejs")]
    public ToolAutoItem NodeJs { get; init; } = new()
    {
        Enabled = true,
        Name = "Node.js",
        InstallUrl = "https://nodejs.org/dist/v22.11.0/",
        MinVersion = "20.0"
    };

    [JsonPropertyName("dotnet_sdk")]
    public ToolAutoItem DotNetSdk { get; init; } = new()
    {
        Enabled = true,
        Name = ".NET SDK",
        InstallUrl = "https://dotnet.microsoft.com/download",
        MinVersion = "10.0"
    };

    [JsonPropertyName("ffmpeg")]
    public ToolAutoItem Ffmpeg { get; init; } = new()
    {
        Enabled = false,
        Name = "FFmpeg",
        InstallUrl = "https://ffmpeg.org/download.html"
    };

    [JsonPropertyName("ripgrep")]
    public ToolAutoItem Ripgrep { get; init; } = new()
    {
        Enabled = true,
        Name = "ripgrep",
        InstallUrl = "https://github.com/BurntSushi/ripgrep/releases",
        MinVersion = "14.0"
    };

    [JsonPropertyName("fd")]
    public ToolAutoItem Fd { get; init; } = new()
    {
        Enabled = true,
        Name = "fd-find",
        InstallUrl = "https://github.com/sharkdp/fd/releases",
        MinVersion = "9.0"
    };

    [JsonPropertyName("jq")]
    public ToolAutoItem Jq { get; init; } = new()
    {
        Enabled = true,
        Name = "jq",
        InstallUrl = "https://github.com/jqlang/jq/releases",
        MinVersion = "1.7"
    };

    [JsonPropertyName("delta")]
    public ToolAutoItem Delta { get; init; } = new()
    {
        Enabled = false,
        Name = "git-delta",
        InstallUrl = "https://github.com/dandavison/delta/releases"
    };

    [JsonPropertyName("bat")]
    public ToolAutoItem Bat { get; init; } = new()
    {
        Enabled = false,
        Name = "bat",
        InstallUrl = "https://github.com/sharkdp/bat/releases"
    };
}

public sealed class ToolAutoItem
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("install_url")]
    public string InstallUrl { get; init; } = "";

    [JsonPropertyName("auto_install_distro")]
    public string? AutoInstallDistro { get; init; }

    [JsonPropertyName("min_version")]
    public string? MinVersion { get; init; }

    [JsonPropertyName("executable_path")]
    public string? ExecutablePath { get; init; }
}
