using System.Text.Json;
using LTAI.Core.Configuration;
using LTAI.Core.Governors;

namespace LTAI.Core.Setup;

/// <summary>
/// 判断是否为首次运行 / 未配置状态。
/// 检查项：配置文件是否存在、L0/L1/L2 是否已配置、本地模型是否已下载。
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

        // 3. 检查 L0 (嵌入层)
        var l0Provider = options.AI.L0.Provider;
        var l0Model = options.AI.L0.Model;
        status.L0Configured = !string.IsNullOrEmpty(l0Provider) && !string.IsNullOrEmpty(l0Model);
        if (!status.L0Configured)
            status.MissingLayers.Add("L0 (Embedding)");

        // 4. 检查 L1 (Fast)
        var l1Provider = options.AI.L1.Provider;
        var l1Model = options.AI.L1.Model;
        status.L1Configured = !string.IsNullOrEmpty(l1Provider) && !string.IsNullOrEmpty(l1Model);
        if (!status.L1Configured)
            status.MissingLayers.Add("L1 (Fast)");

        // 5. 检查 L2 (Deep)
        var l2Provider = options.AI.L2.Provider;
        var l2Model = options.AI.L2.Model;
        status.L2Configured = !string.IsNullOrEmpty(l2Provider) && !string.IsNullOrEmpty(l2Model);
        if (!status.L2Configured)
            status.MissingLayers.Add("L2 (Deep)");

        // 6. 检查是否有至少一个可用的 Provider（有 API key 或本地模型文件）
        if (l0Provider == "local")
        {
            var modelPath = Path.Combine(AppContext.BaseDirectory, "models", "l0", "model.onnx");
            if (!File.Exists(modelPath))
            {
                status.MissingLayers.Add("L0 local model (models/l0/model.onnx not downloaded)");
                status.L0Configured = false;
            }
        }
        else if (!string.IsNullOrEmpty(l0Provider))
        {
            var hasApiKey = options.AI.Providers.TryGetValue(l0Provider, out var p) && !string.IsNullOrEmpty(p.ApiKey);
            if (!hasApiKey)
            {
                var envKey = Environment.GetEnvironmentVariable($"{l0Provider.ToUpperInvariant()}_API_KEY");
                if (string.IsNullOrEmpty(envKey))
                {
                    status.MissingLayers.Add($"L0 API key ({l0Provider} — 未设置 API_KEY)");
                    status.L0Configured = false;
                }
            }
        }

        // 7. 综合判断
        status.IsFirstRun = status.MissingLayers.Count > 0;
        if (status.IsFirstRun)
            status.Reason = $"未配置层: {string.Join(", ", status.MissingLayers)}";

        return status;
    }

    /// <summary>
    /// 便捷方法：检查并可选打印诊断信息
    /// </summary>
    public static void PrintDiagnostics(FirstRunStatus status)
    {
        Console.WriteLine("=== LTAI 配置诊断 ===");
        Console.WriteLine($"  L0 (Embedding): {(status.L0Configured ? "✓ 已配置" : "✗ 未配置")}");
        Console.WriteLine($"  L1 (Fast):      {(status.L1Configured ? "✓ 已配置" : "✗ 未配置")}");
        Console.WriteLine($"  L2 (Deep):      {(status.L2Configured ? "✓ 已配置" : "✗ 未配置")}");
        if (status.IsFirstRun)
        {
            Console.WriteLine();
            Console.WriteLine($"  原因: {status.Reason}");
            Console.WriteLine("  运行 'ltai setup' 或启动配置向导完成初始化。");
        }
        Console.WriteLine();
    }
}

public sealed class FirstRunStatus
{
    public bool IsFirstRun { get; set; }
    public string Reason { get; set; } = "";
    public bool L0Configured { get; set; }
    public bool L1Configured { get; set; }
    public bool L2Configured { get; set; }
    public List<string> MissingLayers { get; set; } = new();
}
