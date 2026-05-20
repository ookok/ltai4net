using System.Collections.Concurrent;
using LTAI.Capability.Tools;
using LTAI.Core.Interfaces;
using LTAI.Core.Messaging;
using LTAI.Core.System;

namespace LTAI.AI.Governors;

public enum CapCategory
{
    Tool, Skill, Mcp, Role, User, Llm, Vfs, Organ, Search, Knowledge, Custom
}

public sealed class CapParam
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";
    public string Description { get; set; } = "";
    public bool Required { get; set; }
    public object? Default { get; set; }
}

public sealed class Capability
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public CapCategory Category { get; set; }
    public string Description { get; set; } = "";
    public List<CapParam> Params { get; set; } = new();
    public Dictionary<string, object> Returns { get; set; } = new();
    public Func<Dictionary<string, object?>, Task<object?>>? Handler { get; set; }
    public bool IsAvailable { get; set; } = true;
    public List<string> Tags { get; set; } = new();
    public string Source { get; set; } = "";

    public string PromptFragment()
    {
        var ps = string.Join(", ", Params.Take(3).Select(p => $"{p.Name}:{p.Type}"));
        var desc = Description.Length > 100 ? Description[..100] : Description;
        return $"{Id}: {desc} (params: {ps})";
    }
}

public sealed record CapInvokeResult
{
    public bool Success { get; set; }
    public object? Data { get; set; }
    public string? Error { get; set; }
    public long LatencyMs { get; set; }
    public string AdapterName { get; set; } = "";
}

public abstract class CapabilityAdapter
{
    public CapCategory Category { get; }
    protected readonly Dictionary<string, Capability> Caps = new();
    private int _invokeCount;

    protected CapabilityAdapter(CapCategory category) => Category = category;

    public abstract Task<List<Capability>> DiscoverAsync();
    public int InvokeCount => _invokeCount;

    public void Register(Capability cap)
    {
        Caps[cap.Id] = cap;
        cap.Category = Category;
    }

    public Capability? Get(string id) => Caps.GetValueOrDefault(id);

    public async Task<CapInvokeResult> InvokeAsync(string id, Dictionary<string, object?> parameters)
    {
        var cap = Get(id);
        if (cap?.Handler == null)
            return new CapInvokeResult { Success = false, Error = $"Capability not found: {id}" };

        Interlocked.Increment(ref _invokeCount);
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await cap.Handler(parameters);
            return new CapInvokeResult { Success = true, Data = result, LatencyMs = sw.ElapsedMilliseconds, AdapterName = Category.ToString() };
        }
        catch (Exception ex)
        {
            return new CapInvokeResult { Success = false, Error = ex.Message, LatencyMs = sw.ElapsedMilliseconds };
        }
    }

    public List<Capability> ListAll() => Caps.Values.ToList();
}

public sealed class CapabilityBus
{
    private static readonly Lazy<CapabilityBus> _instance = new(() => new CapabilityBus());
    public static CapabilityBus Instance => _instance.Value;

    private readonly List<CapabilityAdapter> _adapters = new();
    private readonly ConcurrentDictionary<string, CapInvokeResult> _history = new();
    private readonly IToolRegistry? _toolRegistry;
    private int _totalInvokes;

    private CapabilityBus() { }

    public void Mount(CapabilityAdapter adapter)
    {
        _adapters.Add(adapter);
    }

    public async Task<CapInvokeResult> InvokeAsync(string capId, Dictionary<string, object?>? parameters = null)
    {
        parameters ??= new();
        Interlocked.Increment(ref _totalInvokes);

        foreach (var adapter in _adapters)
        {
            var cap = adapter.Get(capId);
            if (cap != null)
                return await adapter.InvokeAsync(capId, parameters);
        }

        foreach (var adapter in _adapters)
        {
            var fullId = $"{adapter.Category.ToString().ToLower()}:{capId}";
            var cap = adapter.Get(fullId);
            if (cap != null)
                return await adapter.InvokeAsync(fullId, parameters);
        }

        if (_toolRegistry != null && _toolRegistry.HasTool(capId))
        {
            var sw = global::System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await _toolRegistry.InvokeAsync(capId, parameters);
                return new CapInvokeResult { Success = true, Data = result, LatencyMs = sw.ElapsedMilliseconds, AdapterName = "ToolRegistry" };
            }
            catch (Exception ex)
            {
                return new CapInvokeResult { Success = false, Error = ex.Message };
            }
        }

        return new CapInvokeResult { Success = false, Error = $"Unknown capability: {capId}" };
    }

    public async Task<List<Capability>> DiscoverAllAsync()
    {
        var all = new List<Capability>();
        foreach (var adapter in _adapters)
            all.AddRange(await adapter.DiscoverAsync());
        return all;
    }

    public CapInvokeResult? GetHistory(string capId)
    {
        return _history.TryGetValue(capId, out var r) ? r : null;
    }

    public Dictionary<string, object> GetStats()
    {
        return new()
        {
            ["total_invokes"] = _totalInvokes,
            ["adapters"] = _adapters.Count,
            ["caps"] = _adapters.Sum(a => a.ListAll().Count),
            ["by_adapter"] = _adapters.ToDictionary(a => a.Category.ToString(), a => (object)new { caps = a.ListAll().Count, invokes = a.InvokeCount })
        };
    }
}

public sealed class ToolCapAdapter : CapabilityAdapter
{
    private readonly ToolMarket _market;
    public ToolCapAdapter(ToolMarket market) : base(CapCategory.Tool) { _market = market; }

    public override async Task<List<Capability>> DiscoverAsync()
    {
        var specs = _market.Discover();
        return await Task.FromResult(specs.Select(s => new Capability
        {
            Id = $"tool:{s.Name}", Name = s.Name, Category = CapCategory.Tool,
            Description = s.Description, Source = "tool_market",
            Handler = async p => await _market.Execute(s.Name, p?.ToDictionary(k => k.Key, v => v.Value) ?? new())
        }).ToList());
    }
}

public sealed class SkillCapAdapter : CapabilityAdapter
{
    public SkillCapAdapter() : base(CapCategory.Skill) { }

    public override Task<List<Capability>> DiscoverAsync()
    {
        var caps = new List<Capability>();
        foreach (var (name, skill) in UnifiedRegistry.Instance.Skills)
        {
            caps.Add(new Capability
            {
                Id = $"skill:{name}", Name = name, Category = CapCategory.Skill,
                Description = skill.Description, Source = "skill_registry",
                Handler = async _ => await Task.FromResult<object?>(skill.PromptTemplate)
            });
        }
        return Task.FromResult(caps);
    }
}

public sealed class VfsCapAdapter : CapabilityAdapter
{
    private readonly VfsAdapter _vfs;
    public VfsCapAdapter(VfsAdapter vfs) : base(CapCategory.Vfs) { _vfs = vfs; }

    public override async Task<List<Capability>> DiscoverAsync()
    {
        var caps = new List<Capability>
        {
            new() { Id = "vfs:read", Name = "read", Category = CapCategory.Vfs, Description = "Read a file from VFS", Handler = async p => await _vfs.ReadAsync(p?.GetValueOrDefault("path")?.ToString() ?? "/") },
            new() { Id = "vfs:write", Name = "write", Category = CapCategory.Vfs, Description = "Write content to VFS", Handler = async p => await Task.FromResult<object?>(await _vfs.WriteAsync(p?.GetValueOrDefault("path")?.ToString() ?? "/", p?.GetValueOrDefault("content")?.ToString() ?? "")) },
            new() { Id = "vfs:list", Name = "list", Category = CapCategory.Vfs, Description = "List VFS directory", Handler = async p => await Task.FromResult<object?>(await _vfs.ListAsync(p?.GetValueOrDefault("path")?.ToString() ?? "/")) },
            new() { Id = "vfs:delete", Name = "delete", Category = CapCategory.Vfs, Description = "Delete from VFS", Handler = async p => { _vfs.Delete(p?.GetValueOrDefault("path")?.ToString() ?? "/"); return await Task.FromResult<object?>(true); } },
            new() { Id = "vfs:exists", Name = "exists", Category = CapCategory.Vfs, Description = "Check VFS existence", Handler = async p => await _vfs.ExistsAsync(p?.GetValueOrDefault("path")?.ToString() ?? "/") },
            new() { Id = "vfs:search", Name = "search", Category = CapCategory.Vfs, Description = "Search VFS", Handler = async p => await Task.FromResult<object?>(await _vfs.SearchAsync(p?.GetValueOrDefault("path")?.ToString() ?? "/", p?.GetValueOrDefault("query")?.ToString() ?? "")) }
        };
        return await Task.FromResult(caps);
    }
}
