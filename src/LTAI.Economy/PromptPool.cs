using System.Collections.Concurrent;

namespace LTAI.Economy;

public sealed record PoolPrompt(
    string Id,
    string Category,
    string Content,
    double Weight,
    double SuccessRate,
    int TimesUsed,
    DateTime LastUsed)
{
    public double EffectiveScore => Weight * SuccessRate * Math.Exp(-0.1 * (DateTime.UtcNow - LastUsed).TotalHours);
    public bool ShouldRetire => TimesUsed > 100 && SuccessRate < 0.1;
}

public sealed class PromptPool
{
    private readonly ConcurrentDictionary<string, PoolPrompt> _prompts = new();
    private readonly ConcurrentDictionary<string, int> _categoryUsage = new();
    private readonly List<string> _defaultCategories = new()
    {
        "scheduling", "tiling", "layout", "unrolling",
        "type_casting", "memory", "compute", "fusion", "general"
    };
    private int _totalSamples;

    public PromptPool()
    {
        SeedDefaultPrompts();
    }

    public string Sample()
    {
        var prompts = _prompts.Values.Where(p => !p.ShouldRetire).ToList();
        if (prompts.Count == 0) return GetFallbackPrompt();

        var totalScore = prompts.Sum(p => p.EffectiveScore);
        if (totalScore <= 0) return prompts[Random.Shared.Next(prompts.Count)].Content;

        var threshold = Random.Shared.NextDouble() * totalScore;
        double cumulative = 0;
        foreach (var p in prompts)
        {
            cumulative += p.EffectiveScore;
            if (cumulative >= threshold)
            {
                RecordUsage(p);
                return p.Content;
            }
        }

        var selected = prompts.Last();
        RecordUsage(selected);
        return selected.Content;
    }

    public string SampleByCategory(string category)
    {
        var categoryPrompts = _prompts.Values
            .Where(p => p.Category == category && !p.ShouldRetire)
            .ToList();

        if (categoryPrompts.Count == 0)
            return Sample();

        var selected = categoryPrompts[Random.Shared.Next(categoryPrompts.Count)];
        RecordUsage(selected);
        return selected.Content;
    }

    public string SampleUniform()
    {
        _totalSamples++;

        var categories = _defaultCategories
            .Select(c => (Category: c, Count: _categoryUsage.GetValueOrDefault(c, 0)))
            .OrderBy(x => x.Count)
            .ToList();

        var leastUsed = categories.First().Category;
        _categoryUsage.AddOrUpdate(leastUsed, 1, (_, v) => v + 1);

        return SampleByCategory(leastUsed);
    }

    public void AddPrompt(string category, string content, double weight = 1.0)
    {
        var id = $"prompt_{Guid.NewGuid():N}";
        _prompts[id] = new PoolPrompt(
            id, category, content, weight, 1.0, 0, DateTime.MinValue);
    }

    public void RecordFeedback(string promptId, bool success, double latencyImprovement = 0)
    {
        if (!_prompts.TryGetValue(promptId, out var prompt)) return;

        var newSuccessRate = prompt.SuccessRate * 0.9 + (success ? 0.1 : 0);
        var newWeight = prompt.Weight * 0.95 + (latencyImprovement > 0 ? latencyImprovement * 0.05 : 0);

        _prompts[promptId] = prompt with
        {
            TimesUsed = prompt.TimesUsed + 1,
            SuccessRate = Math.Clamp(newSuccessRate, 0.01, 1.0),
            Weight = Math.Clamp(newWeight, 0.1, 5.0),
            LastUsed = DateTime.UtcNow
        };
    }

    public Dictionary<string, int> GetCategoryDistribution()
    {
        return _categoryUsage.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public List<PoolPrompt> GetActivePrompts()
    {
        return _prompts.Values
            .Where(p => !p.ShouldRetire)
            .OrderByDescending(p => p.EffectiveScore)
            .ToList();
    }

    public List<PoolPrompt> GetTopPrompts(int count = 10)
    {
        return _prompts.Values
            .OrderByDescending(p => p.EffectiveScore)
            .Take(count)
            .ToList();
    }

    public void RetireLowPerformers()
    {
        var lowPerformers = _prompts.Values
            .Where(p => p.ShouldRetire)
            .Select(p => p.Id)
            .ToList();

        foreach (var id in lowPerformers)
            _prompts.TryRemove(id, out _);
    }

    private void RecordUsage(PoolPrompt prompt)
    {
        _totalSamples++;
        _categoryUsage.AddOrUpdate(prompt.Category, 1, (_, v) => v + 1);
    }

    private string GetFallbackPrompt()
    {
        return """
            Optimize the following code for lower execution latency while preserving functional correctness.
            Focus on implementation-level improvements:
            - Loop unrolling for parameter reuse
            - Fine-grained memory and compute scheduling
            - Eliminate redundant type casts
            - Tiling to align with vector register granularity
            - Reduce implicit layout transformations
            Do not change the algorithm's mathematical properties or security guarantees.
            """;
    }

    private void SeedDefaultPrompts()
    {
        var defaults = new Dictionary<string, string[]>
        {
            ["scheduling"] = new[]
            {
                "Optimize the compute and memory scheduling. Tile the computation to allow the compiler to hide memory latency behind compute operations. Adjust data access patterns to enable overlapping of off-chip memory loads with on-chip computation.",
                "Reorganize the execution order to maximize the overlap between data movement and computation. Consider splitting large tensors to hide off-chip load latency.",
                "Apply finer-grained scheduling by partitioning operations. Split one large tensor into multiple smaller blocks to allow the compiler to interleave computation and memory access more effectively."
            },
            ["tiling"] = new[]
            {
                "Apply tiling optimization to align tensor shapes with the vector register granularity of (8,128). Ensure tensor dimensions are aligned to these boundaries for maximum VReg utilization.",
                "Reshape and tile the input tensors so that operations fully occupy vector registers. Adjust block sizes to match the target hardware's register file width.",
                "Experiment with different tiling strategies. Try halving the tensor dimensions to trigger XLA compiler optimizations that improve VReg utilization."
            },
            ["layout"] = new[]
            {
                "Optimize tensor layout to eliminate implicit transformations. Check if the current layout forces the compiler to insert unnecessary transpose or reshape operations and remove them.",
                "Rearrange tensor dimensions to avoid layout mismatches between consecutive operations. Ensure the output layout of one operation matches the expected input layout of the next.",
                "Adjust tensor layout to minimize data reformatting overhead. Use contiguous memory layouts to maximize memory bandwidth utilization."
            },
            ["unrolling"] = new[]
            {
                "Apply loop unrolling to reuse common parameters across multiple iterations. Unroll the main computation loop by a factor that maximizes parameter reuse while fitting within on-chip memory.",
                "Unroll the inner loop to eliminate redundant parameter loads. The target parameters should fit within available on-chip memory. Try unroll factors of 4 or 8.",
                "Transform serial loops into unrolled operations to reduce loop overhead and enable the compiler to better optimize the instruction schedule."
            },
            ["type_casting"] = new[]
            {
                "Eliminate unnecessary type casts. Check if the dynamic range of data can be represented with the current precision type. Remove redundant bitcast operations that the XLA compiler can optimize away.",
                "Minimize data type conversions. Verify that all explicit type casts are necessary given the actual data range. Remove casts where the existing type already captures the value range.",
                "Reduce precision of intermediate computations where safe. Consider using bf16 instead of int32 for operations where the precision difference is negligible for the final result."
            },
            ["memory"] = new[]
            {
                "Reduce off-chip memory access by maximizing on-chip memory reuse. Keep frequently accessed data in vector registers or scratchpad memory when possible.",
                "Optimize memory access patterns for contiguous reads and writes. Arrange data layouts to enable burst transfers rather than scattered accesses.",
                "Prefetch data into on-chip memory before it is needed. Structure the computation to allow the hardware prefetcher to effectively predict and load upcoming data."
            },
            ["compute"] = new[]
            {
                "Maximize compute unit utilization by ensuring operations are large enough to amortize launch overhead. Merge small operations into larger ones where dependencies allow.",
                "Balance compute across available processing units. Ensure the workload is evenly distributed across lanes to avoid idle units.",
                "Convert scalar operations to vector operations where possible. Use SIMD-style operations to process multiple data elements per instruction."
            },
            ["fusion"] = new[]
            {
                "Fuse adjacent operations to eliminate intermediate memory writes. Look for producer-consumer operation pairs that can be merged into a single kernel.",
                "Combine multiple element-wise operations into a single fused kernel. This reduces memory traffic and kernel launch overhead.",
                "Identify operation chains that can be fused by the compiler. Restructure the code to make fusion opportunities more visible to the XLA optimizer."
            },
            ["general"] = new[]
            {
                "Identify and eliminate any redundant computations. Cache intermediate results that are used multiple times. Remove dead code and unnecessary operations.",
                "Review the code for performance anti-patterns. Look for operations that cause excessive memory allocation, unnecessary data copies, or suboptimal execution paths.",
                "Optimize the overall code structure: reduce the number of kernel launches, minimize host-device data transfers, and ensure all operations are properly parallelized."
            }
        };

        foreach (var (category, prompts) in defaults)
        {
            foreach (var prompt in prompts)
            {
                AddPrompt(category, prompt, 1.0);
            }
        }
    }
}
