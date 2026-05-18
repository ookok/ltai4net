using System.Text.RegularExpressions;
using LTAI.Vector.Knowledge.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Vector.Knowledge;

public class QueryDecomposer
{
    private readonly ILogger<QueryDecomposer> _logger;

    public QueryDecomposer(ILogger<QueryDecomposer> logger)
    {
        _logger = logger;
    }

    public DecomposedQuery Decompose(string query, int maxSub = 5)
    {
        if (query.Length < 20)
            return new DecomposedQuery(query, new() { new(query, 1.0, "factual") }, "", "direct");

        var subQueries = RuleDecompose(query);
        if (subQueries.Count == 0)
            subQueries.Add(query);

        var result = subQueries.Select(q =>
        {
            var intent = DetectIntent(q);
            return new SubQuery(q, 1.0, intent);
        }).Take(maxSub).ToList();

        if (result.Count == 0) result.Add(new(query, 1.0, "factual"));

        return new DecomposedQuery(query, result, "", result.Count > 1 ? "decompose" : "direct");
    }

    public static List<string> RuleDecompose(string query)
    {
        // Split on Chinese connectors
        var splits = Regex.Split(query, @"\s*(?:与|以及|和|对比|比较)\s*");
        if (splits.Length > 1 && splits.All(s => s.Length > 2))
            return splits.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();

        // Split on Chinese commas and semicolons
        splits = Regex.Split(query, @"[；;，,。]+");
        if (splits.Length > 1 && splits.All(s => s.Length > 3))
            return splits.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();

        return new() { query };
    }

    public static string DetectIntent(string query)
    {
        if (Regex.IsMatch(query, @"^(如何|怎样|步骤|方法|怎么做)"))
            return "procedural";
        if (Regex.IsMatch(query, @"(对比|区别|比较|vs\.?)"))
            return "comparative";
        if (Regex.IsMatch(query, @"^(定义|什么是|概念)"))
            return "definitional";
        return "factual";
    }
}
