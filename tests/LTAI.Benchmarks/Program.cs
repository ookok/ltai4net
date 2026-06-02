// Copyright (c) LTAI. All rights reserved.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using LTAI.AI;

namespace LTAI.Benchmarks;

/// <summary>
/// P11.2: LocalEmbedder perf benchmarks. Run with:
///   dotnet run -c Release --project tests/LTAI.Benchmarks -- --filter "*LocalEmbedder*"
/// Skip (returns baseline=0) if no model file is present in the standard
/// models/ directory.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]   // 1 quick run per config; switch to [MediumRunJob] for CI
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
        _embedder = new LocalEmbedder();
        if (!_embedder.Available)
        {
            // Model not present; benchmarks will return zeros / throw
            return;
        }

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

public class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "smoke")
        {
            // Quick smoke test (no BenchmarkDotNet) — just verify batched API works
            using var embedder = new LocalEmbedder();
            if (!embedder.Available)
            {
                Console.WriteLine("[smoke] LocalEmbedder not available (no model file). Skipping.");
                return;
            }
            var inputs = new[] { "hello world", "C# async patterns", "machine learning transformer" };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var vectors = embedder.GenerateBatch(inputs);
            sw.Stop();
            Console.WriteLine($"[smoke] GenerateBatch({inputs.Length}) → {vectors.Count} vectors, {sw.ElapsedMilliseconds}ms");
            foreach (var (text, vec) in inputs.Zip(vectors))
            {
                Console.WriteLine($"  '{text}' → dim={vec.Length}, L2={MathF.Sqrt(vec.Sum(f => f * f)):F4}");
            }
            return;
        }
        BenchmarkRunner.Run<LocalEmbedderBenchmarks>();
    }
}
