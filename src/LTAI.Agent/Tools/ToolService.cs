using System.Collections.Concurrent;
using LTAI.Core.Messaging;
using LTAI.Knowledge.Core;
using LTAI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

public sealed class ToolService
{
    private readonly ToolLoader _loader;
    private readonly MarkdownToolExecutor _executor;
    private readonly ILogger<ToolService> _logger;
    private readonly ConcurrentDictionary<string, MkTool> _tools = new();
    private readonly ConcurrentDictionary<string, List<MkTool>> _byDomain = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<MkTool>> _byTag = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<MkTool> _cachedAll = Array.Empty<MkTool>();
    private bool _loaded;

    public ToolService(
        ToolLoader loader,
        MarkdownToolExecutor executor,
        ILogger<ToolService> logger,
        IServiceProvider? serviceProvider = null)
    {
        _loader = loader;
        _executor = executor;
        _logger = logger;
        _executor.SetToolResolver(Resolve);
    }

    public bool IsLoaded => _loaded;
    public IReadOnlyList<MkTool> AllTools => _cachedAll;
    public int Count => _tools.Count;

    public async Task LoadAllAsync(string? directory = null, CancellationToken ct = default)
    {
        var dir = directory ?? ResolveToolsDirectory();
        if (!Directory.Exists(dir))
        {
            _logger.LogDebug("Tools directory not found: {Dir}", dir);
            _loaded = true;
            return;
        }

        var files = Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories);
        _logger.LogInformation("Loading {Count} tool files from {Dir}", files.Length, dir);

        await Parallel.ForEachAsync(files, ct, async (file, innerCt) =>
        {
            var tool = await _loader.LoadAsync(file, innerCt).ConfigureAwait(false);
            if (tool != null)
            {
                _tools[tool.Name] = tool;
                _logger.LogDebug("Loaded tool: {Name} ({Type}) from {File}", tool.Name, tool.Type, file);
            }
        }).ConfigureAwait(false);

        RebuildIndexes();
        _loaded = true;
    }

    private void RebuildIndexes()
    {
        _byDomain.Clear();
        _byTag.Clear();

        foreach (var tool in _tools.Values)
        {
            _byDomain.GetOrAdd(tool.Domain, _ => new()).Add(tool);
            foreach (var tag in tool.Tags)
                _byTag.GetOrAdd(tag, _ => new()).Add(tool);
        }

        _cachedAll = _tools.Values.OrderBy(t => t.Domain).ThenBy(t => t.Name).ToList().AsReadOnly();

        foreach (var tool in _tools.Values)
        {
            foreach (var step in tool.Steps)
            {
                if (step.ToolRef != null && !_tools.ContainsKey(step.ToolRef))
                    _logger.LogWarning("Tool '{Tool}' references '{Ref}' which is not loaded", tool.Name, step.ToolRef);
            }
        }
    }

    public MkTool? Resolve(string name)
    {
        _tools.TryGetValue(name, out var tool);
        return tool;
    }

    public MkTool? FindByName(string name)
    {
        _tools.TryGetValue(name, out var tool);
        return tool;
    }

    public List<MkTool> FindByTrigger(string query)
    {
        var results = new List<(MkTool Tool, float Score)>();
        foreach (var tool in _tools.Values)
        {
            float score = 0;
            foreach (var trigger in tool.Triggers)
            {
                if (query.Contains(trigger.Pattern, StringComparison.OrdinalIgnoreCase))
                    score += trigger.Weight;
            }
            if (score > 0)
                results.Add((tool, score));
        }
        return results.OrderByDescending(r => r.Score).Select(r => r.Tool).ToList();
    }

    public List<MkTool> FindByTag(string tag)
    {
        if (_byTag.TryGetValue(tag, out var list))
            return list.ToList();
        return new List<MkTool>();
    }

    public List<MkTool> FindByDomain(string domain)
    {
        if (_byDomain.TryGetValue(domain, out var list))
            return list.ToList();
        return new List<MkTool>();
    }

    public async Task<object?> ExecuteAsync(string toolName, Dictionary<string, object?> args)
    {
        if (!_tools.TryGetValue(toolName, out var tool))
        {
            _logger.LogWarning("Tool '{ToolName}' not found", toolName);
            return new { error = $"Tool '{toolName}' not found" };
        }

        var filled = MarkdownToolExecutor.ValidateAndFillArgs(tool, args);
        return await _executor.ExecuteAsync(tool, filled).ConfigureAwait(false);
    }

    public async Task<MkTool> CreateAndSaveAsync(
        string name,
        MkToolType type,
        string description,
        string template,
        string domain = "general",
        List<ToolParam>? parameters = null,
        List<MkToolTrigger>? triggers = null,
        List<string>? tags = null,
        CancellationToken ct = default)
    {
        var tool = MkTool.Create(name, type, description, template, domain);
        if (parameters != null) tool.Parameters.AddRange(parameters);
        if (triggers != null) tool.Triggers.AddRange(triggers);
        if (tags != null) tool.Tags.AddRange(tags);

        await _loader.SaveAsync(tool, ct: ct).ConfigureAwait(false);
        _tools[tool.Name] = tool;
        _byDomain.GetOrAdd(tool.Domain, _ => new()).Add(tool);
        foreach (var tag in tool.Tags)
            _byTag.GetOrAdd(tag, _ => new()).Add(tool);
        RebuildIndexes();
        _logger.LogInformation("Created tool: {Name}", tool.Name);
        return tool;
    }

    public async Task RegisterIntoRegistryAsync(AIToolRegistry registry)
    {
        foreach (var tool in _tools.Values)
        {
            if (tool.SourceFile == null) continue;

            var handler = CreateHandler(tool);
            await registry.RegisterAsync($"md:{tool.Name}", handler).ConfigureAwait(false);
            _logger.LogDebug("Registered MD tool into registry: {Name}", tool.Name);
        }
    }

    public Func<Dictionary<string, object?>, Task<object?>> CreateHandler(MkTool tool)
    {
        return async args =>
        {
            try
            {
                var filled = MarkdownToolExecutor.ValidateAndFillArgs(tool, args);
                return await _executor.ExecuteAsync(tool, filled).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tool handler failed: {ToolName}", tool.Name);
                return new { error = ex.Message };
            }
        };
    }

    private static string ResolveToolsDirectory()
    {
        var candidates = new[]
        {
            OptionService.Get("paths.tools") ?? Path.Combine(AppContext.BaseDirectory, "tools"),
            Path.Combine(Directory.GetCurrentDirectory(), "tools")
        };

        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir)) return dir;
        }

        return candidates[0];
    }
}
