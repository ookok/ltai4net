using Microsoft.Agents.AI.Hyperlight;

namespace LTAI.Agent.CodeAct;

public enum CodeActApprovalMode { NeverRequire, AlwaysRequire }

public sealed class CodeActConfig
{
    public string[] Tools { get; set; } = Array.Empty<string>();
    public CodeActApprovalMode ApprovalMode { get; set; } = CodeActApprovalMode.NeverRequire;
    public string[] FileMounts { get; set; } = Array.Empty<string>();
    public string[] AllowedDomains { get; set; } = Array.Empty<string>();
}

public sealed class CodeActProvider
{
    private readonly CodeActConfig _config;
    private HyperlightCodeActProvider? _provider;
    private HyperlightExecuteCodeFunction? _function;

    public CodeActProvider(CodeActConfig config) => _config = config;

    public bool IsAvailable => Initialize();

    private bool Initialize()
    {
        if (_provider != null || _function != null) return true;
        try
        {
            _provider = new HyperlightCodeActProvider();
            return true;
        }
        catch
        {
            try
            {
                _function = new HyperlightExecuteCodeFunction();
                return true;
            }
            catch { return false; }
        }
    }

    public HyperlightCodeActProvider? AsProvider() { Initialize(); return _provider; }
    public HyperlightExecuteCodeFunction? AsFunction() { Initialize(); return _function; }
    public bool IsProviderMode => _provider != null;

    public Dictionary<string, string> GetInstructions()
    {
        return new()
        {
            ["pattern"] = "CodeAct (Hyperlight micro-VM)",
            ["description"] = "Model writes Python code block, chains tool calls via call_tool(), runs in isolated Hyperlight VM",
            ["benefits"] = "~50% latency reduction, ~60% token savings",
            ["tools"] = string.Join(", ", _config.Tools),
            ["approval_mode"] = _config.ApprovalMode.ToString(),
            ["file_mounts"] = _config.FileMounts.Length > 0 ? string.Join(", ", _config.FileMounts) : "none",
            ["allowed_domains"] = _config.AllowedDomains.Length > 0 ? string.Join(", ", _config.AllowedDomains) : "none"
        };
    }

    public static Dictionary<string, string> BenchmarkComparison => new()
    {
        ["traditional_latency"] = "27.81s", ["codeact_latency"] = "13.23s",
        ["latency_reduction"] = "52.4%", ["traditional_tokens"] = "6890",
        ["codeact_tokens"] = "2489", ["token_reduction"] = "63.9%"
    };
}

public static class LTAICodeActIntegration
{
    public static CodeActConfig CreateDefaultConfig() => new()
    {
        Tools = new[] { "web_fetch", "knowledge_search", "code_analyze", "doc_parse", "vector_search", "search", "text_extract", "llm_chat" },
        ApprovalMode = CodeActApprovalMode.NeverRequire
    };

    public static CodeActConfig CreateEIAConfig() => new()
    {
        Tools = new[] { "web_fetch", "knowledge_search", "doc_parse", "text_extract", "gaussian_plume", "noise_attenuation", "tabular_reason" },
        AllowedDomains = new[] { "api.github.com", "www.haian.gov.cn" },
        FileMounts = new[] { "/host/data" }
    };

    public static CodeActConfig CreateSecurityConfig() => new()
    {
        Tools = new[] { "web_fetch", "code_analyze", "search" },
        ApprovalMode = CodeActApprovalMode.AlwaysRequire,
        AllowedDomains = new[] { "api.github.com:GET" }
    };
}
