using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;


namespace LTAI.Benchmarks;

[MemoryDiagnoser]
public class LTAIBenchmarks
{

    private string _largeText = "";

    [GlobalSetup]
    public void Setup()
    {

        _largeText = string.Join("\n\n", Enumerable.Repeat(
            "This is a paragraph about artificial intelligence and machine learning systems. " +
            "The field has evolved significantly over the past decade with breakthroughs in deep learning and transformer architectures. " +
            "Modern AI systems can process natural language, generate code, analyze images, and reason about complex problems.", 500));
    }

}

public class Program
{
    public static void Main(string[] args)
    {
        BenchmarkRunner.Run<LTAIBenchmarks>();
    }
}
