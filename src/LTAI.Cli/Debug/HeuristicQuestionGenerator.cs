using System;
using System.Collections.Generic;
using System.Linq;

namespace LTAI.Cli.Debug;

/// <summary>
/// 测试用例难度级别
/// </summary>
public enum TestDifficulty
{
    Simple,     // 简单问题 (缓存/反射路径)
    Moderate,   // 中等复杂度 (L1 本地推理)
    Complex,    // 复杂问题 (RecursiveMAS)
    OOD         // 超出分布 (需要 L2)
}

/// <summary>
/// 测试领域分类
/// </summary>
public enum TestDomain
{
    Math,
    Code,
    Reasoning,
    Creative,
    Factual
}

/// <summary>
/// 启发式测试用例
/// </summary>
public sealed record HeuristicTestCase
{
    public string Query { get; init; } = "";
    public TestDifficulty Difficulty { get; init; }
    public TestDomain Domain { get; init; }
    public string ExpectedRoute { get; init; } = "";
    public string Description { get; init; } = "";
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// 启发式问题生成器
/// 按难度/领域/模式自动生成测试用例
/// </summary>
public sealed class HeuristicQuestionGenerator
{
    private static readonly Dictionary<TestDifficulty, string[]> DifficultyTemplates = new()
    {
        [TestDifficulty.Simple] = new[]
        {
            "你好", "谢谢", "再见", "你是谁", "1+1等于几", 
            "What is 2+2?", "Say hello", "Who are you?"
        },
        [TestDifficulty.Moderate] = new[]
        {
            "解释一下什么是递归", "用 C# 写一个快速排序", 
            "比较 Python 和 JavaScript 的异同", "什么是机器学习?",
            "Explain the concept of closures in programming",
            "Write a function to reverse a string"
        },
        [TestDifficulty.Complex] = new[]
        {
            "分析这段代码的时间复杂度并优化: [代码]",
            "设计一个支持高并发的分布式缓存系统架构",
            "证明勾股定理并给出三种不同的证明方法",
            "Compare and contrast microservices vs monolithic architecture",
            "Implement a thread-safe producer-consumer pattern with backpressure"
        },
        [TestDifficulty.OOD] = new[]
        {
            "预测 2030 年 AI 对全球经济的具体影响 (需引用最新数据)",
            "分析量子计算对当前密码学体系的颠覆性影响及应对策略",
            "Evaluate the ethical implications of AGI deployment in healthcare",
            "Propose a novel algorithm for neural architecture search with O(n) complexity"
        }
    };

    private static readonly Dictionary<TestDomain, string[]> DomainPrompts = new()
    {
        [TestDomain.Math] = new[] { "计算", "证明", "求解方程", "数学" },
        [TestDomain.Code] = new[] { "代码", "实现", "算法", "优化", "debug" },
        [TestDomain.Reasoning] = new[] { "分析", "比较", "为什么", "推理" },
        [TestDomain.Creative] = new[] { "创作", "写一个故事", "设计", "想象" },
        [TestDomain.Factual] = new[] { "什么是", "解释", "历史", "科学" }
    };

    /// <summary>
    /// 生成指定数量的测试用例
    /// </summary>
    public List<HeuristicTestCase> GenerateTests(int count = 20, TestDifficulty? difficulty = null, TestDomain? domain = null)
    {
        var tests = new List<HeuristicTestCase>();
        var rng = new Random();

        var difficulties = difficulty.HasValue ? new[] { difficulty.Value } : Enum.GetValues<TestDifficulty>();
        var domains = domain.HasValue ? new[] { domain.Value } : Enum.GetValues<TestDomain>();

        while (tests.Count < count)
        {
            var d = difficulties[rng.Next(difficulties.Length)];
            var dom = domains[rng.Next(domains.Length)];
            var templates = DifficultyTemplates[d];
            var template = templates[rng.Next(templates.Length)];

            // 简单变异以增加多样性
            var query = MutateQuery(template, rng, dom);

            tests.Add(new HeuristicTestCase
            {
                Query = query,
                Difficulty = d,
                Domain = dom,
                ExpectedRoute = GetExpectedRoute(d),
                Description = $"[{d}] [{dom}] {query}",
                Metadata = new Dictionary<string, object>
                {
                    ["GeneratedAt"] = DateTime.UtcNow,
                    ["Template"] = template
                }
            });
        }

        return tests;
    }

    private static string MutateQuery(string template, Random rng, TestDomain domain)
    {
        // 简单变异策略
        var mutations = new[]
        {
            template,
            domain switch
            {
                TestDomain.Math => $"用中文{template}",
                TestDomain.Code => $"请用 Python {template}",
                TestDomain.Reasoning => $"详细{template}",
                _ => template
            },
            $"[测试] {template}"
        };

        return mutations[rng.Next(mutations.Length)];
    }

    private static string GetExpectedRoute(TestDifficulty difficulty)
    {
        return difficulty switch
        {
            TestDifficulty.Simple => "cache_hit|reflex",
            TestDifficulty.Moderate => "local_llm|recursive_",
            TestDifficulty.Complex => "recursive_|delegate_l2",
            TestDifficulty.OOD => "delegate_l2",
            _ => "unknown"
        };
    }
}
