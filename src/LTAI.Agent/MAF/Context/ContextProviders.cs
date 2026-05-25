namespace LTAI.Agent.Context;

public enum ContextProviderType { Memory, Knowledge, File, MCP }

public sealed class ContextItem
{
    public string Content { get; set; } = "";
    public ContextProviderType ProviderType { get; set; }
    public double Relevance { get; set; }
    public string Source { get; set; } = "";
}

public abstract class LTAIContextProvider(string name, ContextProviderType type)
{
    public string Name { get; } = name;
    public ContextProviderType Type { get; } = type;
    public abstract Task<IReadOnlyList<ContextItem>> GetContextAsync(string query, CancellationToken ct = default);
}

public sealed class MoEContextProvider : LTAIContextProvider
{
    private readonly Func<string, string, Task<object>> _moeQuery;

    public MoEContextProvider(Func<string, string, Task<object>> moeQuery)
        : base("ContextMoE", ContextProviderType.Memory)
    {
        _moeQuery = moeQuery;
    }

    public override async Task<IReadOnlyList<ContextItem>> GetContextAsync(string query, CancellationToken ct = default)
    {
        try
        {
            var moeResult = await _moeQuery("context_provider", query);
            var content = moeResult?.ToString() ?? "";
            if (string.IsNullOrEmpty(content) || content == "ContextMoE memory enrichment active")
            {
                content = $"MoE context enrichment: query='{query[..global::System.Math.Min(query.Length, 80)]}'";
            }
            return new[] { new ContextItem { Content = content, ProviderType = ContextProviderType.Memory, Relevance = 0.85, Source = "MoE" } };
        }
        catch { return Array.Empty<ContextItem>(); }
    }
}

public sealed class CompositeContextProvider : LTAIContextProvider
{
    private readonly List<LTAIContextProvider> _providers;

    public CompositeContextProvider(params LTAIContextProvider[] providers) : base("Composite", ContextProviderType.Memory)
    {
        _providers = providers.ToList();
    }

    public override async Task<IReadOnlyList<ContextItem>> GetContextAsync(string query, CancellationToken ct = default)
    {
        var tasks = _providers.Select(p => p.GetContextAsync(query, ct));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(r => r).OrderByDescending(c => c.Relevance).Take(20).ToList();
    }
}
