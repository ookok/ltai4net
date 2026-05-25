using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tools.Tools;

public record ToolSpec(string Name, string Description, string Type, Dictionary<string, object> InputSchema,
    string Category, double Rating);

public sealed class ToolMarket
{
    private readonly Dictionary<string, ToolSpec> _tools = new();
    private readonly Dictionary<string, Func<Dictionary<string, object>, object?, Task<object?>>> _handlers = new();
    private object? _world;
    private readonly ILogger<ToolMarket> _logger;

    public ToolMarket(ILogger<ToolMarket>? logger = null)
    {
        _logger = logger ?? NullLogger<ToolMarket>.Instance;
        RegisterSeedTools();
    }

    public void SetWorld(object? world) => _world = world;

    public void Register(ToolSpec spec, Func<Dictionary<string, object>, object?, Task<object?>>? handler = null)
    {
        _tools[spec.Name] = spec;
        if (handler != null) _handlers[spec.Name] = handler;
    }

    public List<ToolSpec> Discover() => _tools.Values.OrderBy(t => t.Category).ThenBy(t => t.Name).ToList();

    public ToolSpec? Get(string name) => _tools.GetValueOrDefault(name);

    public List<ToolSpec> Search(string query)
    {
        var lower = query.ToLowerInvariant();
        return _tools.Values
            .Where(t => t.Name.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
                        t.Description.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
                        t.Category.Contains(lower, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.Rating)
            .ToList();
    }

    public async Task<object?> Execute(string name, Dictionary<string, object> inputData)
    {
        if (!_tools.ContainsKey(name)) return new { error = $"Tool not found: {name}" };
        if (!_handlers.TryGetValue(name, out var handler)) return new { error = $"No handler for: {name}" };

        try
        {
            var result = await handler(inputData, _world).ConfigureAwait(false);
            return new { success = true, data = result };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool execution failed: {ToolName}", name);
            return new { success = false, error = ex.Message };
        }
    }

    private void RegisterSeedTools()
    {
        Register(new ToolSpec("gaussian_plume", "Gaussian plume model for air dispersion", "physics",
            new Dictionary<string, object> { ["stability"] = "A-F", ["wind_speed"] = "m/s", ["emission_rate"] = "g/s" },
            "physics", 4.0), GaussianPlume);

        Register(new ToolSpec("noise_attenuation", "Noise attenuation with distance", "physics",
            new Dictionary<string, object> { ["source_level"] = "dB", ["distance"] = "m" },
            "physics", 3.5), NoiseAttenuation);

        Register(new ToolSpec("tabular_reason", "Classify tabular data quality levels", "analysis",
            new Dictionary<string, object> { ["data"] = "array of values", ["type"] = "water/air/noise" },
            "analysis", 4.0), TabularReason);

        Register(new ToolSpec("text_stats", "Compute text statistics", "analysis",
            new Dictionary<string, object> { ["text"] = "string" }, "analysis", 3.0), TextStats);

        Register(new ToolSpec("json_transform", "Transform JSON with path expressions", "data",
            new Dictionary<string, object> { ["data"] = "object", ["path"] = "dot.path" },
            "data", 3.5), JsonTransform);
    }

    private static Task<object?> GaussianPlume(Dictionary<string, object> input, object? world)
    {
        var stability = input.GetValueOrDefault("stability", "D")?.ToString() ?? "D";
        var windSpeed = GetDouble(input, "wind_speed", 3.0);
        var emissionRate = GetDouble(input, "emission_rate", 100.0);

        var stabFactors = new Dictionary<string, double> { ["A"] = 0.22, ["B"] = 0.16, ["C"] = 0.11, ["D"] = 0.08, ["E"] = 0.06, ["F"] = 0.04 };
        var sigY = stabFactors.GetValueOrDefault(stability, 0.08) * 100;
        var sigZ = sigY * 0.5;

        var concentration = emissionRate / (Math.PI * windSpeed * sigY * sigZ);
        return Task.FromResult<object?>(new { concentration_g_m3 = Math.Round(concentration, 4), sigma_y = sigY, sigma_z = sigZ });
    }

    private static Task<object?> NoiseAttenuation(Dictionary<string, object> input, object? world)
    {
        var sourceLevel = GetDouble(input, "source_level", 80.0);
        var distance = GetDouble(input, "distance", 100.0);
        var attenuated = sourceLevel - 20 * Math.Log10(Math.Max(distance, 1)) - 8;
        return Task.FromResult<object?>(new { attenuated_db = Math.Round(attenuated, 1), distance_m = distance });
    }

    private static Task<object?> TabularReason(Dictionary<string, object> input, object? world)
    {
        input.TryGetValue("data", out var dataRaw);
        var data = dataRaw is JsonElement el && el.ValueKind == JsonValueKind.Array
            ? el.EnumerateArray().Select(e => e.GetDouble()).ToList()
            : new List<double>();

        var type = input.GetValueOrDefault("type", "water")?.ToString() ?? "water";
        var avg = data.Count > 0 ? data.Average() : 0;

        var thresholds = type switch
        {
            "water" => new Dictionary<string, double> { ["I"] = 1, ["II"] = 3, ["III"] = 5, ["IV"] = 10, ["V"] = 20 },
            "air" => new Dictionary<string, double> { ["优"] = 50, ["良"] = 100, ["轻度"] = 150, ["中度"] = 200 },
            "noise" => new Dictionary<string, double> { ["0类"] = 50, ["1类"] = 55, ["2类"] = 60, ["3类"] = 65, ["4类"] = 70 },
            _ => new Dictionary<string, double> { ["阈值"] = 100 }
        };

        var level = thresholds.Where(t => avg <= t.Value).OrderBy(t => t.Value).FirstOrDefault();
        return Task.FromResult<object?>(new { average = Math.Round(avg, 2), level = level.Key, type });
    }

    private static Task<object?> TextStats(Dictionary<string, object> input, object? world)
    {
        var text = input.GetValueOrDefault("text", "")?.ToString() ?? "";
        var words = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return Task.FromResult<object?>(new
        {
            chars = text.Length,
            chars_no_space = text.Count(c => !char.IsWhiteSpace(c)),
            words = words.Length,
            lines = text.Split('\n').Length
        });
    }

    private static Task<object?> JsonTransform(Dictionary<string, object> input, object? world)
    {
        input.TryGetValue("data", out var data);
        var path = input.GetValueOrDefault("path", "")?.ToString() ?? "";
        if (data is JsonElement element)
        {
            var parts = path.Split('.');
            foreach (var part in parts)
            {
                if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(part, out var prop))
                    element = prop;
                else break;
            }
            return Task.FromResult<object?>(JsonSerializer.Deserialize<object>(element.GetRawText()));
        }
        return Task.FromResult<object?>(data);
    }

    private static double GetDouble(Dictionary<string, object> dict, string key, double defaultValue)
    {
        if (dict.TryGetValue(key, out var val) && val is JsonElement je && je.ValueKind == JsonValueKind.Number)
            return je.GetDouble();
        if (val != null && double.TryParse(val.ToString(), out var d)) return d;
        return defaultValue;
    }
}
