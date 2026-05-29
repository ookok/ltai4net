using System.Text.Json;
using LTAI.Core.Configuration;
using LTAI.Core.Governors;

namespace LTAI.Core.Setup;

/// <summary>
/// 判断是否为首次运行 / 未配置状态。
/// 检查项：配置文件是否存在、Provider 是否已配置。
/// L0/L1/L2 不再单独检查 — 由 Provider + Mode 自动推导。
/// </summary>
public static class FirstRunDetector
{
    public static FirstRunStatus Check(string configPath)
    {
        var status = new FirstRunStatus();

        // 1. 配置文件存在且非空
        if (!File.Exists(configPath) || new FileInfo(configPath).Length < 30)
        {
            status.IsFirstRun = true;
            status.Reason = "配置文件不存在或为空";
            return status;
        }

        // 2. 解析配置
        LTAIOptions options;
        try
        {
            var json = File.ReadAllText(configPath);
            var doc = JsonDocument.Parse(json);
            var section = doc.RootElement.TryGetProperty("LTAI", out var ltai) ? ltai : default;
            if (section.ValueKind == JsonValueKind.Undefined)
            {
                status.IsFirstRun = true;
                status.Reason = "配置文件中缺少 LTAI 节点";
                return status;
            }

            options = section.Deserialize<LTAIOptions>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch
        {
            status.IsFirstRun = true;
            status.Reason = "配置文件 JSON 解析失败";
            return status;
        }

        // 3. 检查 Provider (L0/L1/L2 由 Provider + Mode 自动推导)
        var provider = options.AI.Provider;
        status.ProviderConfigured = !string.IsNullOrEmpty(provider);
        if (!status.ProviderConfigured)
            status.MissingLayers.Add("AI provider");

        // 4. 综合判断
        status.IsFirstRun = status.MissingLayers.Count > 0;
        if (status.IsFirstRun)
            status.Reason = $"缺少配置: {string.Join(", ", status.MissingLayers)}";

        return status;
    }

    /// <summary>
    /// 便捷方法：检查并可选打印诊断信息
    /// </summary>
    public static void PrintDiagnostics(FirstRunStatus status)
    {
        Console.WriteLine("=== LTAI 配置诊断 ===");
        Console.WriteLine($"  Provider: {(status.ProviderConfigured ? "✓ 已配置" : "✗ 未配置")}");
        if (status.IsFirstRun)
        {
            Console.WriteLine();
            Console.WriteLine($"  原因: {status.Reason}");
            Console.WriteLine("  配置 LTAI.AI.provider='deepseek' 后启动。");
        }
        Console.WriteLine();
    }
}

public sealed class FirstRunStatus
{
    public bool IsFirstRun { get; set; }
    public string Reason { get; set; } = "";
    public bool ProviderConfigured { get; set; }
    public List<string> MissingLayers { get; set; } = new();
}
