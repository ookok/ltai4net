// Copyright (c) LTAI. All rights reserved.

using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using LTAI.AI;
using LTAI.Agent.Pipeline;
using LTAI.Agent.Pipeline.Steps;

namespace LTAI.Benchmarks;

/// <summary>
/// P14.4: LocalEmbedder perf benchmark for the active model (MiniLM-L6-v2 INT8).
/// Default mode runs a fast, self-managed benchmark (5 scenarios × 50 iterations)
/// and prints a clean latency + alloc table. CPU-only for stable numbers.
/// Use <c>bdn</c> for the full BenchmarkDotNet report.
///
/// Run with:
///   dotnet run -c Release --project tests/LTAI.Benchmarks             (default: benchmark)
///   dotnet run -c Release --project tests/LTAI.Benchmarks -- smoke    (1 call, wiring check)
///   dotnet run -c Release --project tests/LTAI.Benchmarks -- bdn      (BDN full report)
/// </summary>
public static class Program
{
    private const string Model = "minilm-l6-v2";
    private const string Quant = "int8";
    private const int Warmup = 3;
    private const int Iterations = 50;
    private const string GpuPref = "cpu";

    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "smoke")
        {
            await RunSmokeAsync();
            return;
        }
        if (args.Length > 0 && args[0] == "bdn")
        {
            BenchmarkRunner.Run<LocalEmbedderBenchmarks>();
            return;
        }
        await RunComparisonAsync();
    }

    private static async Task RunComparisonAsync()
    {
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
        Console.WriteLine($" P14.4: LocalEmbedder {Model} {Quant.ToUpperInvariant()} benchmark (CPU-only, {Iterations} iterations)");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
        Console.WriteLine();

        var row = await RunOneAsync(Model, Quant);
        if (row == null)
        {
            Console.WriteLine("No data collected.");
            return;
        }

        PrintTable(new List<Row> { row });
    }

    private static async Task<Row?> RunOneAsync(string model, string quant)
    {
        Console.Write($"  {model,-15} {quant,-5} ... ");
        try
        {
            LocalEmbedder.DefaultDisabled = false;
            LocalEmbedder.Options = new EmbeddingOptions
            {
                Gpu = GpuPref,
                Quantization = quant,
                Models = { [model] = quant }
            };

            using var embedder = new LocalEmbedder();
            if (!embedder.Available)
            {
                Console.WriteLine("skipped (not available)");
                return null;
            }
            if (!string.Equals(embedder.CurrentModelName, model, StringComparison.OrdinalIgnoreCase))
            {
                if (!embedder.SwitchModel(model))
                {
                    Console.WriteLine("skipped (SwitchModel failed)");
                    return null;
                }
            }

            var actualQuant = embedder.UsingQuantizedModel ? "int8" : "fp32";
            var actualModel = embedder.CurrentModelName ?? "?";
            var actualEP = embedder.ActiveExecutionProvider ?? "?";
            var dim = embedder.Dim;

            var shortText = "C# async await Task Parallel Library patterns";
            var mediumText = string.Join(" ", Enumerable.Repeat(
                "machine learning neural network deep learning transformer attention mechanism", 20));
            var batch8 = Enumerable.Range(0, 8).Select(i => $"function_{i} read_file pattern_{i} test").ToArray();
            var batch32 = Enumerable.Range(0, 32).Select(i => $"operation_{i} handle_task scenario_{i}").ToArray();
            var batch128 = Enumerable.Range(0, 128).Select(i => $"tool_{i} description_{i} agent_capability_{i}").ToArray();

            var sShort = Measure(() => embedder.Generate(shortText), Warmup, Iterations);
            var sMedium = Measure(() => embedder.Generate(mediumText), Warmup, Iterations);
            var b8 = Measure(() => embedder.GenerateBatch(batch8), Warmup, Iterations);
            var b32 = Measure(() => embedder.GenerateBatch(batch32), Warmup, Iterations);
            var b128 = Measure(() => embedder.GenerateBatch(batch128), Warmup, Iterations);

            Console.WriteLine($"OK ({actualEP}, dim={dim})");

            return new Row
            {
                Model = actualModel,
                Quant = actualQuant,
                EP = actualEP,
                Dim = dim,
                Short_Mean = sShort.mean, Short_Median = sShort.median,
                Medium_Mean = sMedium.mean, Medium_Median = sMedium.median,
                B8_Mean = b8.mean, B8_Median = b8.median, B8_AllocPerCall = b8.allocPerCall,
                B32_Mean = b32.mean, B32_Median = b32.median, B32_AllocPerCall = b32.allocPerCall,
                B128_Mean = b128.mean, B128_Median = b128.median, B128_AllocPerCall = b128.allocPerCall
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex.Message}");
            return null;
        }
    }

    private static (double mean, double median, long allocPerCall) Measure<T>(Func<T> action, int warmup, int iters)
    {
        for (int i = 0; i < warmup; i++) action();

        var times = new double[iters];
        long allocStart = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iters; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            times[i] = sw.Elapsed.TotalMilliseconds;
        }
        long allocEnd = GC.GetAllocatedBytesForCurrentThread();

        Array.Sort(times);
        var mean = times.Average();
        var median = iters % 2 == 0 ? (times[iters / 2 - 1] + times[iters / 2]) / 2.0 : times[iters / 2];
        var allocPerCall = (allocEnd - allocStart) / iters;
        return (mean, median, allocPerCall);
    }

    private static void PrintTable(List<Row> rows)
    {
        Console.WriteLine();
        Console.WriteLine(" Latency (mean ms) per call:");
        Console.WriteLine("┌─────────────────┬──────┬─────┬────┬──────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ Model           │ Quant│ EP  │ Dim│ Short     Medium    B8        B32       B128                │");
        Console.WriteLine("├─────────────────┼──────┼─────┼────┼──────────────────────────────────────────────────────────────┤");
        foreach (var r in rows)
        {
            Console.WriteLine(
                $"│ {r.Model,-15} │ {r.Quant,-4} │ {r.EP,-3} │ {r.Dim,3}│ " +
                $"{r.Short_Mean,8:F3}  {r.Medium_Mean,8:F3}  {r.B8_Mean,8:F3}  {r.B32_Mean,8:F3}  {r.B128_Mean,8:F3}            │");
        }
        Console.WriteLine("└─────────────────┴──────┴─────┴────┴──────────────────────────────────────────────────────────────┘");

        Console.WriteLine();
        Console.WriteLine(" Memory allocated per call (bytes):");
        Console.WriteLine("┌─────────────────┬──────┬─────────────────┬─────────────────┬─────────────────┐");
        Console.WriteLine("│ Model           │ Quant│ B8 alloc/call   │ B32 alloc/call  │ B128 alloc/call │");
        Console.WriteLine("├─────────────────┼──────┼─────────────────┼─────────────────┼─────────────────┤");
        foreach (var r in rows)
        {
            Console.WriteLine(
                $"│ {r.Model,-15} │ {r.Quant,-4} │ {r.B8_AllocPerCall,15:N0} │ {r.B32_AllocPerCall,15:N0} │ {r.B128_AllocPerCall,15:N0} │");
        }
        Console.WriteLine("└─────────────────┴──────┴─────────────────┴─────────────────┴─────────────────┘");
    }

    private static void PrintSpeedup(List<Row> rows) { /* single-combo mode: no comparison */ }

    private static string Ratio(double fp32, double int8) => ""; // unused in single-combo mode

    private static async Task RunSmokeAsync()
    {
        Console.WriteLine($"[smoke] 1 call on {Model} {Quant} to verify wiring");
        LocalEmbedder.DefaultDisabled = false;
        LocalEmbedder.Options = new EmbeddingOptions
        {
            Gpu = GpuPref,
            Quantization = Quant,
            Models = { [Model] = Quant }
        };
        try
        {
            using var embedder = new LocalEmbedder();
            Console.WriteLine($"  DefaultDisabled={LocalEmbedder.DefaultDisabled}");
            Console.WriteLine($"  BaseModelsDirectory={LocalEmbedder.BaseModelsDirectory}");
            Console.WriteLine($"  CurrentModelName={embedder.CurrentModelName ?? "<null>"}");
            // Force Available check (triggers load)
            try
            {
                var _ = embedder.Available;
            }
            catch (Exception loadEx)
            {
                Console.WriteLine($"  LOAD THREW: {loadEx.GetType().Name}: {loadEx.Message}");
                if (loadEx.InnerException != null) Console.WriteLine($"    inner: {loadEx.InnerException.Message}");
                return;
            }
            Console.WriteLine($"  Available={embedder.Available}");
            if (embedder.LastLoadError != null)
            {
                Console.WriteLine($"  LastLoadError: {embedder.LastLoadError.GetType().Name}: {embedder.LastLoadError.Message}");
                if (embedder.LastLoadError.InnerException != null)
                    Console.WriteLine($"    inner: {embedder.LastLoadError.InnerException.GetType().Name}: {embedder.LastLoadError.InnerException.Message}");
            }
            if (!embedder.Available)
            {
                Console.WriteLine("  skipped (not available — silent load failure, see EnsureLoaded catch)");
                return;
            }
            if (!string.Equals(embedder.CurrentModelName, Model, StringComparison.OrdinalIgnoreCase))
                embedder.SwitchModel(Model);
            var sw = Stopwatch.StartNew();
            var v = embedder.Generate("hello world");
            sw.Stop();
            var l2 = MathF.Sqrt(v.Sum(f => f * f));
            Console.WriteLine($"  dim={v.Length} in {sw.ElapsedMilliseconds}ms  L2={l2:F4}  EP={embedder.ActiveExecutionProvider}  quant={(embedder.UsingQuantizedModel ? "INT8" : "FP32")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  CRASH: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    private class Row
    {
        public string Model = "", Quant = "", EP = "";
        public int Dim;
        public double Short_Mean, Short_Median, Medium_Mean, Medium_Median;
        public double B8_Mean, B8_Median, B32_Mean, B32_Median, B128_Mean, B128_Median;
        public long B8_AllocPerCall, B32_AllocPerCall, B128_AllocPerCall;
    }
}

/// <summary>
/// P11.2: full BDN report (verbose, statistically rigorous). Run with
/// <c>dotnet run -c Release --project tests/LTAI.Benchmarks -- bdn</c>.
/// Use the default mode (no args) for the fast comparison table.
/// </summary>
[MemoryDiagnoser]
[MediumRunJob]
public class LocalEmbedderBenchmarks
{
    private LocalEmbedder _embedder = null!;
    private string _short = "";
    private string _medium = "";
    private string[] _batch8 = null!;
    private string[] _batch32 = null!;
    private string[] _batch128 = null!;

    [GlobalSetup]
    public void Setup()
    {
        LocalEmbedder.Options = new EmbeddingOptions { Gpu = "cpu" };
        _embedder = new LocalEmbedder();
        if (!_embedder.Available) return;

        _short = "C# async await Task Parallel Library patterns";
        _medium = string.Join(" ", Enumerable.Repeat(
            "machine learning neural network deep learning transformer attention mechanism", 20));
        _batch8 = Enumerable.Range(0, 8).Select(i => $"function_{i} read_file pattern_{i} test").ToArray();
        _batch32 = Enumerable.Range(0, 32).Select(i => $"operation_{i} handle_task scenario_{i}").ToArray();
        _batch128 = Enumerable.Range(0, 128).Select(i => $"tool_{i} description_{i} agent_capability_{i}").ToArray();
    }

    [Benchmark(Baseline = true)]
    public float[] Single_ShortText()
    {
        if (!_embedder.Available) return Array.Empty<float>();
        return _embedder.Generate(_short);
    }

    [Benchmark]
    public float[] Single_MediumText()
    {
        if (!_embedder.Available) return Array.Empty<float>();
        return _embedder.Generate(_medium);
    }

    [Benchmark]
    public int Batched_Batch8()
    {
        if (!_embedder.Available) return 0;
        return _embedder.GenerateBatch(_batch8).Count;
    }

    [Benchmark]
    public int Batched_Batch32()
    {
        if (!_embedder.Available) return 0;
        return _embedder.GenerateBatch(_batch32).Count;
    }

    [Benchmark]
    public int Batched_Batch128()
    {
        if (!_embedder.Available) return 0;
        return _embedder.GenerateBatch(_batch128).Count;
    }
}

/// <summary>
/// FastEmb (pure-math embedding) benchmark — no ONNX dependency.
/// Measures LocalEmbedder.FastEmb throughput for CPU-only routing scenarios.
/// Run with: dotnet run -c Release --project tests/LTAI.Benchmarks -- bdn
/// </summary>
[MemoryDiagnoser]
[MediumRunJob]
public class FastEmbBenchmarks
{
    private LTAI.AI.EmbeddingClient _client = null!;
    private string[] _batch8 = null!;
    private string[] _batch32 = null!;

    [GlobalSetup]
    public void Setup()
    {
        var httpFactory = new StubHttpClientFactory();
        _client = new LTAI.AI.EmbeddingClient(
            httpFactory, local: null, logger: null, remoteCache: null);
        _batch8 = Enumerable.Range(0, 8).Select(i => $"search query {i} about code analysis").ToArray();
        _batch32 = Enumerable.Range(0, 32).Select(i => $"how to implement feature {i} in C#").ToArray();
    }

    [Benchmark(Baseline = true)]
    public async Task<float[]> GenerateAsync_Single()
    {
        return await _client.GenerateAsync("C# async await parallel programming patterns");
    }

    [Benchmark]
    public async Task<float[]> GenerateAsync_LongText()
    {
        return await _client.GenerateAsync(string.Join(" ",
            Enumerable.Repeat("machine learning neural network deep learning transformer attention mechanism", 20)));
    }

    [Benchmark]
    public async Task<int> GenerateBatchAsync_8()
    {
        var vecs = await _client.GenerateBatchAsync(_batch8);
        return vecs.Count();
    }

    [Benchmark]
    public async Task<int> GenerateBatchAsync_32()
    {
        var vecs = await _client.GenerateBatchAsync(_batch32);
        return vecs.Count();
    }
}

file sealed class StubHttpClientFactory : System.Net.Http.IHttpClientFactory
{
    public System.Net.Http.HttpClient CreateClient(string name) => new();
}

/// <summary>
/// PipelineRunner benchmark — measures throughput of the post-generation
/// pipeline (GrammarCheckStep only, since other steps are no-op with no
/// tool calls). Run with:
///   dotnet run -c Release --project tests/LTAI.Benchmarks -- bdn
/// </summary>
[MemoryDiagnoser]
[SimpleJob(iterationCount: 10, warmupCount: 3)]
public class PipelineRunnerBenchmarks
{
    private PipelineRunner _runner = null!;
    private MessageContext _validCode = null!;
    private MessageContext _invalidCode = null!;
    private MessageContext _noToolCalls = null!;

    [GlobalSetup]
    public void Setup()
    {
        var tsParser = new LTAI.Agent.CodeAnalysis.TreeSitterParser();

        _runner = new PipelineRunner(
            new IPipelineStep[] { new GrammarCheckStep(
                tsParser: tsParser) });

        _validCode = new MessageContext("test", default);
        _validCode.ToolCalls.Add(("write", "{\"path\":\"test.cs\"}", "class Foo { }"));

        _invalidCode = new MessageContext("test", default);
        _invalidCode.ToolCalls.Add(("write", "{\"path\":\"test.cs\"}", "class Foo {"));

        _noToolCalls = new MessageContext("test", default);
    }

    [Benchmark(Baseline = true)]
    public async Task ValidCodePasses()
    {
        await _runner.RunPostGenerationAsync(_validCode);
    }

    [Benchmark]
    public async Task InvalidCodeBlocked()
    {
        await _runner.RunPostGenerationAsync(_invalidCode);
    }

    [Benchmark]
    public async Task NoToolCalls_EmptyPipeline()
    {
        await _runner.RunPostGenerationAsync(_noToolCalls);
    }
}
