namespace LTAI.AI;

/// <summary>
/// Hardcoded capability database for known models.
/// Used as fallback when /v1/models API doesn't return capabilities.
/// Merged with dynamic API data at runtime by <see cref="ModelMetadataProvider"/>.
/// </summary>
internal static class KnownCapabilities
{
    public static readonly Dictionary<string, (int? ContextWindow, int? MaxOutput, ModelCapability Caps)> All = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── DeepSeek ──
        ["deepseek-v4-flash"] = (1_048_576, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),
        ["deepseek-reasoner"] = (1_048_576, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),
        ["deepseek-v4-pro"] = (1_048_576, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall | ModelCapability.StructuredOutput),
        ["deepseek-v3"] = (65_536, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),
        ["deepseek-embedding"] = (null, null, ModelCapability.Embedding),

        // ── OpenAI ──
        ["gpt-4o"] = (131_072, 16384, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall | ModelCapability.StructuredOutput | ModelCapability.Vision),
        ["gpt-4o-mini"] = (131_072, 16384, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall | ModelCapability.StructuredOutput | ModelCapability.Vision),
        ["gpt-4-turbo"] = (131_072, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall | ModelCapability.Vision),
        ["gpt-4"] = (8_192, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),
        ["gpt-3.5-turbo"] = (16_384, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),
        ["text-embedding-3-small"] = (null, null, ModelCapability.Embedding),
        ["text-embedding-3-large"] = (null, null, ModelCapability.Embedding),
        ["dall-e-3"] = (null, null, ModelCapability.ImageGeneration),

        // ── Anthropic ──
        ["claude-sonnet-4-5"] = (204_800, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall | ModelCapability.StructuredOutput | ModelCapability.Vision),
        ["claude-3-5-sonnet"] = (204_800, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall | ModelCapability.Vision),
        ["claude-3-haiku"] = (204_800, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall | ModelCapability.Vision),
        ["claude-3-opus"] = (204_800, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall | ModelCapability.Vision),

        // ── Aliyun / DashScope ──
        ["qwen-plus"] = (131_072, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall | ModelCapability.StructuredOutput),
        ["qwen-max"] = (32_768, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),
        ["qwen-turbo"] = (131_072, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),
        ["qwen-long"] = (1_048_576, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),
        ["text-embedding-v2"] = (null, null, ModelCapability.Embedding),

        // ── Zhipu ──
        ["glm-4-plus"] = (131_072, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),
        ["glm-4"] = (131_072, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),
        ["glm-4-flash"] = (131_072, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),

        // ── Moonshot ──
        ["moonshot-v1-8k"] = (8_192, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall),
        ["moonshot-v1-32k"] = (32_768, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall),
        ["moonshot-v1-128k"] = (131_072, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall),

        // ── Groq ──
        ["llama-3.3-70b-versatile"] = (131_072, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),
        ["llama-3.3-70b"] = (131_072, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),
        ["llama-3.1-8b"] = (131_072, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),
        ["mixtral-8x7b"] = (32_768, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),

        // ── Mistral ──
        ["mistral-large-latest"] = (131_072, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall | ModelCapability.StructuredOutput),
        ["mistral-small-latest"] = (32_768, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),

        // ── Perplexity ──
        ["sonar-pro"] = (131_072, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall),
        ["sonar"] = (131_072, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall),

        // ── X.AI ──
        ["grok-2-1212"] = (131_072, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall | ModelCapability.Vision),
        ["grok-2"] = (131_072, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall | ModelCapability.Vision),

        // ── Fireworks ──
        ["accounts/fireworks/models/llama-v3p3-70b-instruct"] = (131_072, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),
        ["llama-v3p3"] = (131_072, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),

        // ── Baichuan ──
        ["Baichuan4"] = (32_768, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),

        // ── Yi ──
        ["yi-large"] = (32_768, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),

        // ── StepFun ──
        ["step-2-16k"] = (16_384, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall),

        // ── MiniMax ──
        ["MiniMax-Text-01"] = (131_072, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),

        // ── Cohere ──
        ["command-r-plus"] = (131_072, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),

        // ── Hunyuan ──
        ["hunyuan-pro"] = (32_768, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),

        // ── Doubao ──
        ["ep-XXXXXX"] = (128_000, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),

        // ── SiliconFlow ──
        ["deepseek-ai/DeepSeek-V2.5"] = (131_072, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),
        ["BAAI/bge-large-zh-v1.5"] = (null, null, ModelCapability.Embedding),
        ["BAAI/bge-large-en-v1.5"] = (null, null, ModelCapability.Embedding),

        // ── Ollama (common) ──
        ["llama3.2"] = (131_072, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall),
        ["llama3.1"] = (131_072, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall),
        ["qwen2.5"] = (131_072, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall),
        ["mistral"] = (32_768, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall),

        // ── OpenRouter ──
        ["deepseek/deepseek-v4-flash"] = (1_048_576, 8192, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),

        // ── Together AI ──
        ["mistralai/Mixtral-8x22B-Instruct-v0.1"] = (65_536, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),

        // ── Spark ──
        ["spark-3.5"] = (8_192, 4096, ModelCapability.Chat | ModelCapability.Streaming),

        // ── ERNIE ──
        ["ernie-4.0"] = (131_072, 4096, ModelCapability.Chat | ModelCapability.Streaming | ModelCapability.ToolCall | ModelCapability.FunctionCall),
    };
}
