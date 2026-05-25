using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTAI.Core.Governors;

public sealed class ModelFitter
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static string CachePath => Path.Combine(AppContext.BaseDirectory, "assets", "hf-model-cache.json");

    public record FitResult(
        ModelLayer Layer,
        string ModelId, string Name, double ParamsB,
        string BestQuant, string FitLabel,
        long MemoryMB, long DiskMB,
        long Downloads, string Architecture,
        string DownloadUrl, string MirrorUrl,
        bool IsLocal, bool IsInstalled);

    public static async Task<List<FitResult>> FetchAndScoreAsync()
    {
        var hw = HardwareDetector.Detect();
        var results = new List<FitResult>();

        var hfModels = await FetchFromHFAsync(hw).ConfigureAwait(false);
        results.AddRange(hfModels);

        if (results.Count < 5)
        {
            var fallback = ScoreFromLocalRegistry(hw);
            foreach (var f in fallback)
                if (!results.Any(r => r.ModelId == f.ModelId))
                    results.Add(f);
        }

        var deduped = results
            .GroupBy(r => r.ModelId)
            .Select(g => g.OrderByDescending(r => r.Downloads).First())
            .ToList();

        MarkInstalled(deduped, hw);

        return deduped.OrderBy(r => r.Layer).ThenByDescending(r => FitScore(r)).ToList();
    }

    public static List<FitResult> GetRecommendedForLayer(List<FitResult> all, ModelLayer layer, int topN = 10)
    {
        return all
            .Where(r => r.Layer == layer)
            .OrderByDescending(r => FitScore(r))
            .Take(topN)
            .ToList();
    }

    private static double FitScore(FitResult r) =>
        r.FitLabel switch
        {
            "Perfect" => 100,
            "Good" => 75,
            "Marginal" => 50,
            "Tight" => 25,
            _ => 0
        };

    public static (string Label, double Ratio) RateFit(long recommendedMemoryMB, long availableMemoryMB)
    {
        if (availableMemoryMB <= 0) return ("Unknown", 0);
        var ratio = (double)recommendedMemoryMB / availableMemoryMB;
        var label = ratio <= 0.3 ? "Perfect"
            : ratio <= 0.6 ? "Good"
            : ratio <= 0.9 ? "Marginal"
            : ratio <= 1.4 ? "Tight"
            : "Too Big";
        return (label, ratio);
    }

    private static async Task<List<FitResult>> FetchFromHFAsync(HardwareDetector.HardwareInfo hw)
    {
        var results = new List<FitResult>();
        var availableMB = hw.HasGpu ? hw.VramMB : hw.RamMB;

        try
        {
            var cache = LoadCache();
            if (cache != null && cache.CachedAt > DateTime.UtcNow.AddHours(-6))
                return cache.Results;

            var searchTerms = new[] { "gguf", "bartowski/", "unsloth/" };
            var fetched = new List<HfModel>();
            foreach (var term in searchTerms)
            {
                try
                {
                    var url = $"https://hf-mirror.com/api/models?search={term}&sort=downloads&direction=-1&limit=25&full=true&filter=text-generation";
                    var data = await Client.GetFromJsonAsync<List<HfModel>>(url).ConfigureAwait(false);
                    if (data != null) fetched.AddRange(data);
                }
                catch (Exception ex) { }
            }

            var unique = fetched.DistinctBy(x => x.Id).ToList();
            foreach (var model in unique)
            {
                var (paramsB, arch) = await FetchModelParamsAsync(model.Id).ConfigureAwait(false);
                if (paramsB <= 0 || paramsB > 50) continue;

                var quants = ExtractQuants(model.Siblings.Select(s => s.Filename).ToList());
                var bestQuant = PickBestQuant(quants, (long)(paramsB * 4.85 / 8 * 1024), availableMB);
                var memMB = EstimateMemoryMB(paramsB, bestQuant);

                var (fitLabel, _) = RateFit(memMB, availableMB);
                var layer = DecideLayer(paramsB, arch);

                results.Add(new FitResult(
                    Layer: layer,
                    ModelId: model.Id,
                    Name: model.Id.Split('/').Last(),
                    ParamsB: paramsB,
                    BestQuant: bestQuant,
                    FitLabel: fitLabel,
                    MemoryMB: memMB,
                    DiskMB: EstimateDiskMB(paramsB, bestQuant),
                    Downloads: model.Downloads,
                    Architecture: arch,
                    DownloadUrl: $"https://hf-mirror.com/{model.Id}/resolve/main/{FindBestFile(model.Siblings)}",
                    MirrorUrl: $"https://huggingface.co/{model.Id}/resolve/main/{FindBestFile(model.Siblings)}",
                    IsLocal: false,
                    IsInstalled: false));
            }

            SaveCache(results);
        }
        catch (Exception ex) { }

        return results;
    }

    private static List<FitResult> ScoreFromLocalRegistry(HardwareDetector.HardwareInfo hw)
    {
        var results = new List<FitResult>();
        var availableMB = hw.HasGpu ? hw.VramMB : hw.RamMB;

        foreach (var model in LocalModelRegistry.AvailableModels)
        {
            var (fitLabel, _) = RateFit(model.RecommendedMemoryMB, availableMB);
            results.Add(new FitResult(
                Layer: model.Layer,
                ModelId: model.Version,
                Name: model.Name,
                ParamsB: EstimateParamsFromName(model.Name),
                BestQuant: ExtractQuantFromName(model.Version),
                FitLabel: fitLabel,
                MemoryMB: model.RecommendedMemoryMB,
                DiskMB: model.DiskSizeMB,
                Downloads: 0,
                Architecture: model.EngineType,
                DownloadUrl: model.Url,
                MirrorUrl: model.MirrorUrl,
                IsLocal: true,
                IsInstalled: false));
        }
        return results;
    }

    private static void MarkInstalled(List<FitResult> results, HardwareDetector.HardwareInfo hw)
    {
        var modelsDir = Path.Combine(AppContext.BaseDirectory, "assets", "models");
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var layerDir = Path.Combine(modelsDir, r.Layer == ModelLayer.L0 ? "l0" : r.Layer == ModelLayer.L1 ? "l1" : "l2");
            if (!Directory.Exists(layerDir)) continue;

            var searchPattern = r.IsLocal
                ? $"{r.ModelId}*"
                : $"*{r.Name}*";
            var files = Directory.GetFiles(layerDir, searchPattern, SearchOption.AllDirectories);
            results[i] = r with { IsInstalled = files.Length > 0 };
        }
    }

    private static async Task<(double ParamsB, string Architecture)> FetchModelParamsAsync(string modelId)
    {
        try
        {
            var url = $"https://hf-mirror.com/{modelId}/resolve/main/config.json";
            var config = await Client.GetFromJsonAsync<HfConfig>(url).ConfigureAwait(false);
            if (config == null) return (EstimateParamsFromName(modelId), "");

            var arch = config.Architectures?.FirstOrDefault() ?? "";
            var l = config.NumLayers > 0 ? config.NumLayers : 24;
            var h = config.HiddenSize > 0 ? config.HiddenSize : 2048;
            var i = config.IntermediateSize > 0 ? config.IntermediateSize : h * 4;
            var v = config.VocabSize > 0 ? config.VocabSize : 32000;

            var feedforward = l * 3.0 * h * i;
            var attention = l * 4.0 * h * h;
            var embedding = v * (double)h;
            var total = feedforward + attention + embedding;

            var experts = config.NumLocalExperts > 0 ? config.NumLocalExperts : 1;
            var active = config.ExpertsPerTok > 0 ? config.ExpertsPerTok : experts;
            var paramsB = total * experts * active / experts / 1_000_000_000.0;

            return (paramsB, arch);
        }
        catch (Exception ex) { return (EstimateParamsFromName(modelId), ""); }
    }

    private static double EstimateParamsFromName(string name)
    {
        var lower = (name ?? "").ToLowerInvariant();
        if (lower.Contains("14b")) return 14;
        if (lower.Contains("8b") || lower.Contains("8.0")) return 8;
        if (lower.Contains("7b") || lower.Contains("7.5")) return 7;
        if (lower.Contains("4b") || lower.Contains("4.0")) return 4;
        if (lower.Contains("3b") || lower.Contains("3.2") || lower.Contains("-3b")) return 3;
        if (lower.Contains("2b") || lower.Contains("2.0") || lower.Contains("2.9")) return 2.5;
        if (lower.Contains("1b") || lower.Contains("1.5") || lower.Contains("1.7")) return 1.5;
        if (lower.Contains("500m") || lower.Contains("360m") || lower.Contains("0.5")) return 0.5;
        if (lower.Contains("300m") || lower.Contains("200m")) return 0.3;
        if (lower.Contains("30b") || lower.Contains("32b")) return 32;
        if (lower.Contains("70b") || lower.Contains("72b")) return 70;
        return 3;
    }

    private static string ExtractQuantFromName(string name)
    {
        var upper = (name ?? "").ToUpperInvariant();
        foreach (var q in QuantHierarchy)
            if (upper.Contains(q)) return q;
        return "Q4_K_M";
    }

    private static string PickBestQuant(List<string> quants, long baseMemMB, long availableMB)
    {
        foreach (var q in QuantHierarchy)
        {
            if (!quants.Contains(q)) continue;
            var scaled = (long)(baseMemMB * QScale(q));
            var (label, _) = RateFit(scaled, availableMB);
            if (label is "Perfect" or "Good") return q;
        }
        foreach (var q in QuantHierarchy)
            if (quants.Contains(q)) return q;
        return QuantHierarchy[3];
    }

    private static readonly string[] QuantHierarchy =
        ["Q8_0", "Q6_K", "Q5_K_M", "Q4_K_M", "Q4_K_S", "Q4_0", "Q3_K_M", "Q3_K_S", "Q2_K"];

    private static double QScale(string q) => q switch
    {
        "Q8_0" => 1.0, "Q6_K" => 0.825, "Q5_K_M" => 0.6875,
        "Q4_K_M" => 0.606, "Q4_K_S" => 0.606, "Q4_0" => 0.5625,
        "Q3_K_M" => 0.419, "Q3_K_S" => 0.419, "Q2_K" => 0.331,
        _ => 0.606
    };

    private static long EstimateMemoryMB(double paramsB, string quant)
    {
        var bpw = quant switch
        {
            "Q8_0" => 8.0, "Q6_K" => 6.6, "Q5_K_M" => 5.5, "Q5_K_S" => 5.5,
            "Q4_K_M" => 4.85, "Q4_K_S" => 4.85, "Q4_0" => 4.5,
            "Q3_K_M" => 3.35, "Q3_K_S" => 3.35, "Q2_K" => 2.65,
            _ => 4.85
        };
        return (long)(paramsB * bpw / 8 * 1.15 * 1024);
    }

    private static long EstimateDiskMB(double paramsB, string quant)
    {
        var bpw = quant switch
        {
            "Q8_0" => 8.0, "Q6_K" => 6.6, "Q5_K_M" => 5.5, "Q5_K_S" => 5.5,
            "Q4_K_M" => 4.85, "Q4_K_S" => 4.85, "Q4_0" => 4.5,
            "Q3_K_M" => 3.35, "Q3_K_S" => 3.35, "Q2_K" => 2.65,
            _ => 4.85
        };
        return (long)(paramsB * bpw / 8 * 1024);
    }

    private static List<string> ExtractQuants(List<string> filenames)
    {
        return filenames
            .Select(f => Path.GetFileNameWithoutExtension(f).ToUpperInvariant())
            .Where(n => QuantHierarchy.Any(n.Contains))
            .Select(n => QuantHierarchy.First(n.Contains))
            .Distinct()
            .OrderByDescending(q => Array.IndexOf(QuantHierarchy, q))
            .ToList();
    }

    private static string FindBestFile(List<HfSibling> siblings)
    {
        foreach (var q in QuantHierarchy)
        {
            var file = siblings.FirstOrDefault(s =>
                Path.GetFileNameWithoutExtension(s.Filename).ToUpperInvariant().Contains(q));
            if (file != null) return file.Filename;
        }
        return siblings.FirstOrDefault(s => s.Filename.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))?.Filename
            ?? siblings.FirstOrDefault()?.Filename ?? "";
    }

    private static ModelLayer DecideLayer(double paramsB, string arch)
    {
        if (arch.Contains("MoE", StringComparison.OrdinalIgnoreCase)
            || arch.Contains("mixture", StringComparison.OrdinalIgnoreCase))
            return paramsB > 7 ? ModelLayer.L2 : ModelLayer.L1;
        if (paramsB <= 3) return ModelLayer.L0;
        if (paramsB <= 8) return ModelLayer.L1;
        return ModelLayer.L2;
    }

    public sealed class HfModel
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("downloads")] public long Downloads { get; set; }
        [JsonPropertyName("siblings")] public List<HfSibling> Siblings { get; set; } = new();
    }

    public sealed class HfSibling
    {
        [JsonPropertyName("rfilename")] public string Filename { get; set; } = "";
    }

    public sealed class HfConfig
    {
        [JsonPropertyName("num_hidden_layers")] public int NumLayers { get; set; }
        [JsonPropertyName("hidden_size")] public int HiddenSize { get; set; }
        [JsonPropertyName("intermediate_size")] public int IntermediateSize { get; set; }
        [JsonPropertyName("vocab_size")] public int VocabSize { get; set; }
        [JsonPropertyName("architectures")] public List<string>? Architectures { get; set; }
        [JsonPropertyName("num_local_experts")] public int NumLocalExperts { get; set; }
        [JsonPropertyName("num_experts_per_tok")] public int ExpertsPerTok { get; set; }
    }

    private sealed record CacheStore(DateTime CachedAt, List<FitResult> Results);

    private static CacheStore? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var json = File.ReadAllText(CachePath);
            return JsonSerializer.Deserialize<CacheStore>(json);
        }
        catch (Exception ex) { return null; }
    }

    private static void SaveCache(List<FitResult> results)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(new CacheStore(DateTime.UtcNow, results)));
        }
        catch (Exception ex) { }
    }
}
