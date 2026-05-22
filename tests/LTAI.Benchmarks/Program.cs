using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using LTAI.Tools.CodeEngine;
using LTAI.Tools.Reasoning;
using LTAI.DNA;
using LTAI.Knowledge.Vector.Embedding;
using LTAI.Knowledge.Vector.Interfaces;
using LTAI.Knowledge.Vector.Knowledge;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Benchmarks;

public class LTAIBenchmarks
{
    private MultiLangCodeAnalyzer? _analyzer;
    private MathReasoner? _math;
    private HierarchicalChunker? _chunker;
    private LocalEmbeddingBackend? _embedding;
    private WorldModel? _world;
    private PredictiveEngine? _predictor;

    [GlobalSetup]
    public void Setup()
    {
        var logger = NullLoggerFactory.Instance.CreateLogger("bench");
        _analyzer = new MultiLangCodeAnalyzer(new ParserRegistry(NullLoggerFactory.Instance), new NullLogger<MultiLangCodeAnalyzer>());
        _math = new MathReasoner(new NullLogger<MathReasoner>());
        _chunker = new HierarchicalChunker(new NullLogger<HierarchicalChunker>());
        _embedding = new LocalEmbeddingBackend(new NullLogger<LocalEmbeddingBackend>());
        _world = new WorldModel();
        _predictor = new PredictiveEngine();
    }

    [Benchmark]
#pragma warning disable CS0618
    public void CodeAnalysis_CSharp() => _analyzer!.Analyze(SampleCode.CSharp, CodeLanguage.CSharp);
    [Benchmark]
    public void CodeAnalysis_Python() => _analyzer!.Analyze(SampleCode.Python, CodeLanguage.Python);
#pragma warning restore CS0618
    [Benchmark] public void MathSolve_Linear() => _math!.SolveAsync("3*x+5=20").GetAwaiter().GetResult();
    [Benchmark] public void MathSolve_Expression() => _math!.SolveAsync("123.45 + 67.89").GetAwaiter().GetResult();
    [Benchmark] public void Chunking_Small() => _chunker!.Chunk(SampleText.Small, 1000, 100);
    [Benchmark] public void Chunking_Large() => _chunker!.Chunk(SampleText.Large, 1000, 100);
    [Benchmark] public void Embedding_Single() => _embedding!.EmbedAsync(new[] { "hello world" }).GetAwaiter().GetResult();
    [Benchmark] public void Embedding_Batch10() => _embedding!.EmbedAsync(Enumerable.Repeat("test text for embedding", 10).ToList()).GetAwaiter().GetResult();
    [Benchmark] public void WorldModel_Observe() { for (var i = 0; i < 100; i++) _world!.Observe($"entity{i}", "attr", i); }
    [Benchmark] public void Predictor_Forecast() { for (var i = 0; i < 100; i++) _predictor!.Record("m", i); _predictor!.Forecast("m"); }
}

public static class SampleCode
{
    public const string CSharp = """
        using System;
        using System.Linq;
        using System.Collections.Generic;
        
        namespace Test {
            public class Calculator {
                public double Add(double a, double b) => a + b;
                public double Multiply(double a, double b) => a * b;
                public IEnumerable<double> Fibonacci(int n) {
                    double a = 0, b = 1;
                    for (int i = 0; i < n; i++) { yield return a; double t = a + b; a = b; b = t; }
                }
            }
        }
        """;

    public const string Python = """
        import json
        from typing import List, Dict, Optional
        
        class DataProcessor:
            def __init__(self, config: Dict):
                self.config = config
                self.results = []
            
            def process(self, data: List[Dict]) -> List[Dict]:
                for item in data:
                    if self.validate(item):
                        self.results.append(self.transform(item))
                return self.results
            
            def validate(self, item: Dict) -> bool:
                return all(k in item for k in self.config.get("required", []))
            
            def transform(self, item: Dict) -> Dict:
                return {k: str(v).upper() if isinstance(v, str) else v 
                        for k, v in item.items()}
        """;
}

public static class SampleText
{
    public const string Small = "This is a short document for testing chunking performance.";
    public static readonly string Large = string.Join("\n\n", Enumerable.Repeat("This is a paragraph about artificial intelligence and machine learning systems. " +
        "The field has evolved significantly over the past decade with breakthroughs in deep learning and transformer architectures. " +
        "Modern AI systems can process natural language, generate code, analyze images, and reason about complex problems.", 50));
}

public class Program
{
    public static void Main(string[] args)
    {
        BenchmarkRunner.Run<LTAIBenchmarks>();
    }
}
