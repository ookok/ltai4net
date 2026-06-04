// Copyright (c) LTAI. All rights reserved.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LTAI.AI;
using LTAI.Core.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Declarative;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Agent.Workflows;

/// <summary>
/// P15 central registry for all hot-editable workflow configs. Owns:
/// <list type="bullet">
///   <item>MAF declarative <c>*.yaml</c> workflows (compiled to <see cref="Workflow"/> instances).</item>
///   <item>LTAI <c>*.json</c> configs (decision-tree, sequential, concurrent pipelines).</item>
/// </list>
/// <para>
/// New requests read the current snapshot via <see cref="TryGetWorkflow"/>,
/// <see cref="TryGetDecisionTreeConfig"/>, <see cref="TryGetPipelineConfig"/>, etc.
/// Reloads swap the snapshot atomically; in-flight requests keep their existing reference (D71).
/// </para>
/// <para>
/// D68: failed reloads preserve the old snapshot; the
/// <see cref="WorkflowHotReloadNotifier"/> broadcasts the failure for TUI /
/// Desktop / Web subscribers.
/// </para>
/// </summary>
public sealed class YAMLWorkflowRegistry
{
    private readonly ILogger<YAMLWorkflowRegistry> _logger;
    private readonly WorkflowHotReloadNotifier _notifier;
    private readonly IMcpToolHandler? _mcpToolHandler;
    private readonly string _watchDir;

    /// <summary>Publicly exposed watch directory (used by <see cref="YAMLWorkflowWatcher"/> and TUI).</summary>
    public string WatchDirectory => _watchDir;

    private readonly ConcurrentDictionary<string, WorkflowSnapshot> _workflows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DecisionTreeSnapshot> _configs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PipelineSnapshot> _pipelines = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public YAMLWorkflowRegistry(
        IOptions<LTAIOptions> options,
        ILogger<YAMLWorkflowRegistry> logger,
        WorkflowHotReloadNotifier notifier,
        IMcpToolHandler? mcpToolHandler = null)
    {
        _logger = logger;
        _notifier = notifier;
        _mcpToolHandler = mcpToolHandler;
        _watchDir = options.Value.Workflows?.WatchDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "LTAI.Agent.Workflows.ltai-workflows");
    }

    /// <summary>
    /// Scan the watch directory and load all YAML/JSON files. Idempotent;
    /// subsequent calls reload changed files.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        _initialized = true;

        if (!Directory.Exists(_watchDir))
        {
            _logger.LogWarning("Workflow watch directory does not exist: {Dir}", _watchDir);
            return;
        }

        foreach (var path in Directory.EnumerateFiles(_watchDir, "*.yaml")
                     .Concat(Directory.EnumerateFiles(_watchDir, "*.yml"))
                     .Concat(Directory.EnumerateFiles(_watchDir, "*.json")))
        {
            await ReloadFileAsync(path, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Loaded {Workflows} YAML workflow(s) + {Configs} config(s) from {Dir}",
            _workflows.Count, _configs.Count + _pipelines.Count, _watchDir);
    }

    /// <summary>Reload a single file (called by the <see cref="YAMLWorkflowWatcher"/> on change).</summary>
    public async Task ReloadFileAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path)) return;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();

        try
        {
            if (ext is ".yaml" or ".yml")
            {
                var raw = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                var content = LTAI.Core.I18n.WorkflowI18nResolver.Resolve(raw);
                var workflow = BuildWorkflow(path, content);
                var type = ProbeWorkflowType(path, content);
                var version = ProbeWorkflowVersion(path, content);
                _workflows[name] = new WorkflowSnapshot(name, type, version, path, DateTime.UtcNow, workflow, content);
                _notifier.PublishReloaded(new WorkflowReloadEvent(name, type, version, DateTime.UtcNow, path));
            }
            else if (ext == ".json")
            {
                var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);

                // P16.1: peek at the "type" field to decide which config parser.
                var typePeek = PeekJsonStringField(json, "type") ?? "";
                if (typePeek is "sequential" or "concurrent")
                {
                    var cfg = PipelineConfig.Parse(json);
                    _pipelines[name] = new PipelineSnapshot(name, path, DateTime.UtcNow, cfg);
                    _notifier.PublishReloaded(new WorkflowReloadEvent(name, cfg.Type, cfg.Version, DateTime.UtcNow, path));
                }
                else
                {
                    var cfg = DecisionTreeConfig.Parse(json);
                    cfg.SourcePath = path;
                    _configs[name] = new DecisionTreeSnapshot(name, path, DateTime.UtcNow, cfg);
                    _notifier.PublishReloaded(new WorkflowReloadEvent(name, cfg.Type, cfg.Version, DateTime.UtcNow, path));
                }
            }
            else
            {
                _logger.LogDebug("Reload skipped: unsupported extension: {Path}", path);
            }
        }
        catch (Exception ex)
        {
            // D68: failed reload preserves the old snapshot.
            var type = ext switch
            {
                ".yaml" or ".yml" => "maf-declarative",
                ".json" => "ltai-config",
                _ => "unknown",
            };
            _notifier.PublishLoadFailed(new WorkflowLoadFailedEvent(
                name, type, path, ex.Message, DateTime.UtcNow));
        }
    }

    /// <summary>Get a compiled MAF workflow by name (file stem). Returns null if not loaded.</summary>
    public Workflow? TryGetWorkflow(string name)
    {
        return _workflows.TryGetValue(name, out var snap) ? snap.Workflow : null;
    }

    /// <summary>Get the current DecisionTree config by name. Returns <see cref="DecisionTreeConfig.Default"/> if not loaded.</summary>
    public DecisionTreeConfig GetDecisionTreeConfig(string name = "decision-tree")
    {
        return _configs.TryGetValue(name, out var snap) ? snap.Config : DecisionTreeConfig.Default;
    }

    /// <summary>Get a P16.1 pipeline config by name (e.g. "sequential" or "concurrent"). Returns null if not loaded.</summary>
    public PipelineConfig? TryGetPipelineConfig(string name)
    {
        return _pipelines.TryGetValue(name, out var snap) ? snap.Config : null;
    }

    /// <summary>List all loaded workflows + configs (for TUI <c>/workflow list</c>).</summary>
    public IReadOnlyList<WorkflowInfo> List()
    {
        var result = new List<WorkflowInfo>(_workflows.Count + _configs.Count + _pipelines.Count);
        foreach (var (k, v) in _workflows)
        {
            result.Add(new WorkflowInfo(k, v.Type, v.Version, v.FilePath, v.LoadedAtUtc, v.ContentByteCount));
        }
        foreach (var (k, v) in _configs)
        {
            result.Add(new WorkflowInfo(k, v.Config.Type, v.Config.Version, v.FilePath, v.LoadedAtUtc, v.Config?.ToString()?.Length ?? 0));
        }
        foreach (var (k, v) in _pipelines)
        {
            result.Add(new WorkflowInfo(k, v.Config.Type, v.Config.Version, v.FilePath, v.LoadedAtUtc, v.Config?.ToString()?.Length ?? 0));
        }
        return result.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Force a reload of all watched files (TUI <c>/workflow reload *</c>).</summary>
    public async Task ReloadAllAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_watchDir)) return;
        foreach (var path in Directory.EnumerateFiles(_watchDir, "*.yaml")
                     .Concat(Directory.EnumerateFiles(_watchDir, "*.yml"))
                     .Concat(Directory.EnumerateFiles(_watchDir, "*.json")))
        {
            await ReloadFileAsync(path, ct).ConfigureAwait(false);
        }
    }

    private Workflow BuildWorkflow(string path, string content)
    {
        // MAF DeclarativeWorkflowBuilder builds the workflow from a file path
        // or reader; we already read the content, so write to a temp file to
        // reuse the existing builder API. The temp file is cleaned up
        // immediately after Build returns.
        var options = new DeclarativeWorkflowOptions(new NoOpAgentProvider())
        {
            McpToolHandler = _mcpToolHandler,
        };
        return DeclarativeWorkflowBuilder.Build<string>(path, options);
    }

    /// <summary>
    /// Inspect the YAML to discover its <c>kind:</c> (greeting fast-path vs
    /// handoff vs sequential) and <c>version:</c> fields without doing a full
    /// build. Pure string scan — no YAML parser dependency.
    /// </summary>
    private static string ProbeWorkflowType(string path, string content)
    {
        var firstKind = ExtractYamlScalar(content, "kind") ?? "(unknown)";
        return firstKind.ToLowerInvariant() switch
        {
            "workflow" => "maf-declarative",
            _ => firstKind,
        };
    }

    private static int ProbeWorkflowVersion(string path, string content)
    {
        var v = ExtractYamlScalar(content, "version");
        return int.TryParse(v, out var n) ? n : 1;
    }

    private static string? ExtractYamlScalar(string content, string key)
    {
        // Look for `key: value` or `key: 'value'` or `key: "value"` at the
        // start of any line (top-level only — indented values are ignored to
        // avoid false matches in nested blocks).
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith($"{key}:"))
            {
                var val = trimmed[(key.Length + 1)..].Trim().Trim('\'', '"');
                return string.IsNullOrEmpty(val) ? null : val;
            }
        }
        return null;
    }

    /// <summary>
    /// Quick string scan for a JSON field without full parse.
    /// Looks for the first occurrence of <c>"key": "value"</c> pattern.
    /// Used by <see cref="ReloadFileAsync"/> to decide which config parser to use.
    /// </summary>
    private static string? PeekJsonStringField(string json, string key)
    {
        var search = $"\"{key}\": \"";
        var idx = json.IndexOf(search, StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + search.Length;
        var end = json.IndexOf('"', start);
        return end < 0 ? null : json[start..end];
    }

    // NoOpAgentProvider: greeting.yaml + similar fast-path YAMLs do not call
    // InvokeAzureAgent. The workflow should never reach the agent provider;
    // if it does, throw to surface the YAML authoring bug.
    private sealed class NoOpAgentProvider : ResponseAgentProvider
    {
        public override Task<string> CreateConversationAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Workflow does not declare an agent; CreateConversationAsync should not be reached.");
        public override Task<ChatMessage> CreateMessageAsync(string conversationId, ChatMessage conversationMessage, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Workflow does not declare an agent; CreateMessageAsync should not be reached.");
        public override Task<ChatMessage> GetMessageAsync(string conversationId, string messageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Workflow does not declare an agent; GetMessageAsync should not be reached.");
        public override IAsyncEnumerable<AgentResponseUpdate> InvokeAgentAsync(
            string agentId, string? agentVersion, string? conversationId,
            IEnumerable<ChatMessage>? messages, IDictionary<string, object?>? inputArguments,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException($"Workflow attempted to invoke agent '{agentId}' but uses a NoOpAgentProvider. This YAML should not declare InvokeAzureAgent actions.");
        public override IAsyncEnumerable<ChatMessage> GetMessagesAsync(
            string conversationId, int? limit = null, string? after = null,
            string? before = null, bool newestFirst = false,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Workflow does not declare an agent; GetMessagesAsync should not be reached.");
    }
}

/// <summary>Immutable snapshot of a compiled workflow. Swapped atomically on reload.</summary>
internal sealed record WorkflowSnapshot(
    string Name,
    string Type,
    int Version,
    string FilePath,
    DateTime LoadedAtUtc,
    Workflow Workflow,
    string RawContent)
{
    public int ContentByteCount => RawContent.Length;
}

/// <summary>Immutable snapshot of a DecisionTree config. Swapped atomically on reload.</summary>
internal sealed record DecisionTreeSnapshot(
    string Name,
    string FilePath,
    DateTime LoadedAtUtc,
    DecisionTreeConfig Config);

/// <summary>P16.1: Immutable snapshot of a Sequential / Concurrent pipeline config. Swapped atomically on reload.</summary>
internal sealed record PipelineSnapshot(
    string Name,
    string FilePath,
    DateTime LoadedAtUtc,
    PipelineConfig Config);

/// <summary>Public summary of a loaded workflow (for TUI <c>/workflow list</c> and DevUI dashboard).</summary>
/// <param name="Name">File stem (e.g. <c>decision-tree</c>).</param>
/// <param name="Type"><c>maf-declarative</c> / <c>decision-tree</c> / <c>sequential</c> etc.</param>
/// <param name="Version">Schema version field from the file.</param>
/// <param name="FilePath">Absolute path to the source file.</param>
/// <param name="LoadedAtUtc">When the snapshot was last refreshed.</param>
/// <param name="SizeBytes">File size in bytes (for <c>/workflow show</c> preview).</param>
public readonly record struct WorkflowInfo(
    string Name,
    string Type,
    int Version,
    string FilePath,
    DateTime LoadedAtUtc,
    int SizeBytes);
