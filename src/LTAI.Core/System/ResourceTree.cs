using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.System;

public sealed record MountPoint
{
    public string Path { get; init; }
    public string Name { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();

    public Func<string, Task<string?>>? ReadFn { get; init; }
    public Func<Task<List<object>>>? ListFn { get; init; }
    public Func<string, int, Task<List<object>>>? SearchFn { get; init; }
    public Func<string, string, Task<bool>>? WriteFn { get; init; }

    public MountPoint(string path, string name)
    {
        Path = path;
        Name = name;
    }
}

public sealed record ResourceResult
{
    public string Path { get; init; } = "/";
    public string Operation { get; init; } = "none";
    public string? Content { get; init; }
    public List<object>? Items { get; init; }
    public string? Error { get; init; }
    public long LatencyMs { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}

public sealed class ResourceTree
{
    public static ResourceTree Instance => _instance.Value;
    private static readonly Lazy<ResourceTree> _instance = new(() => new ResourceTree());

    private readonly ConcurrentDictionary<string, MountPoint> _mounts = new();
    private readonly ConcurrentDictionary<string, Dictionary<string, MountPoint>> _snapshots = new();
    private readonly ILogger<ResourceTree> _logger;

    private static readonly Regex s_searchArgsRegex = new(
        @"^(.*?)\s+(.+)$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex s_writeArgsRegex = new(
        @"^(\S+)\s+(.+)", RegexOptions.Singleline | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public ResourceTree(ILogger<ResourceTree>? logger = null)
    {
        _logger = logger ?? NullLogger<ResourceTree>.Instance;
    }

    public void Mount(
        string path,
        string name,
        Func<string, Task<string?>>? readFn = null,
        Func<Task<List<object>>>? listFn = null,
        Func<string, int, Task<List<object>>>? searchFn = null,
        Func<string, string, Task<bool>>? writeFn = null,
        Dictionary<string, string>? metadata = null)
    {
        var mount = new MountPoint(path, name)
        {
            ReadFn = readFn,
            ListFn = listFn,
            SearchFn = searchFn,
            WriteFn = writeFn,
            Metadata = metadata ?? new Dictionary<string, string>()
        };
        _mounts[path] = mount;
        _logger.LogInformation("Mount registered: {Path} ({Name})", path, name);
    }

    public bool Unmount(string path)
    {
        if (_mounts.TryRemove(path, out _))
        {
            _logger.LogInformation("Mount removed: {Path}", path);
            return true;
        }
        return false;
    }

    private (MountPoint? Mount, string SubPath) ResolvePath(string path)
    {
        var normalized = path.TrimEnd('/');
        if (string.IsNullOrEmpty(normalized)) normalized = "/";

        MountPoint? best = null;
        var bestLen = 0;

        var sorted = _mounts.OrderByDescending(kvp => kvp.Key.Length);
        foreach (var kvp in sorted)
        {
            var mountPath = kvp.Key.TrimEnd('/');
            if (normalized.StartsWith(mountPath, StringComparison.OrdinalIgnoreCase)
                && (normalized.Length == mountPath.Length || normalized[mountPath.Length] == '/'))
            {
                best = kvp.Value;
                bestLen = mountPath.Length;
                break;
            }
        }

        var subPath = bestLen > 0 && normalized.Length > bestLen
            ? normalized[bestLen..]
            : "";
        if (string.IsNullOrEmpty(subPath)) subPath = "/";

        return (best, subPath);
    }

    public async Task<ResourceResult> Read(string path)
    {
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (mount, subPath) = ResolvePath(path);
            if (mount?.ReadFn == null)
                return new ResourceResult
                {
                    Path = path,
                    Operation = "read",
                    Error = $"No readable mount found for path: {path}",
                    LatencyMs = sw.ElapsedMilliseconds
                };

            var content = await mount.ReadFn(subPath).ConfigureAwait(false);
            return new ResourceResult
            {
                Path = path,
                Operation = "read",
                Content = content,
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Read failed for {Path}", path);
            return new ResourceResult
            {
                Path = path,
                Operation = "read",
                Error = ex.Message,
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
    }

    public async Task<ResourceResult> List(string path = "/")
    {
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (mount, subPath) = ResolvePath(path);
            if (mount?.ListFn == null)
                return new ResourceResult
                {
                    Path = subPath,
                    Operation = "list",
                    Error = $"No listable mount found for path: {path}",
                    LatencyMs = sw.ElapsedMilliseconds
                };

            var items = await mount.ListFn().ConfigureAwait(false);
            return new ResourceResult
            {
                Path = subPath,
                Operation = "list",
                Items = items,
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "List failed for {Path}", path);
            return new ResourceResult
            {
                Path = path,
                Operation = "list",
                Error = ex.Message,
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
    }

    public async Task<ResourceResult> Search(string path, string query, int topK = 10)
    {
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (mount, subPath) = ResolvePath(path);
            if (mount?.SearchFn == null)
                return new ResourceResult
                {
                    Path = subPath,
                    Operation = "search",
                    Error = $"No searchable mount found for path: {path}",
                    LatencyMs = sw.ElapsedMilliseconds
                };

            var items = await mount.SearchFn(query, topK).ConfigureAwait(false);
            return new ResourceResult
            {
                Path = subPath,
                Operation = "search",
                Items = items,
                Metadata = new Dictionary<string, string>
                {
                    ["query"] = query,
                    ["topK"] = topK.ToString()
                },
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed for {Path}", path);
            return new ResourceResult
            {
                Path = path,
                Operation = "search",
                Error = ex.Message,
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
    }

    public async Task<ResourceResult> Write(string path, string content)
    {
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (mount, subPath) = ResolvePath(path);
            if (mount?.WriteFn == null)
                return new ResourceResult
                {
                    Path = path,
                    Operation = "write",
                    Error = $"No writable mount found for path: {path}",
                    LatencyMs = sw.ElapsedMilliseconds
                };

            var ok = await mount.WriteFn(subPath, content).ConfigureAwait(false);
            return new ResourceResult
            {
                Path = path,
                Operation = "write",
                Content = ok ? "ok" : "failed",
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Write failed for {Path}", path);
            return new ResourceResult
            {
                Path = path,
                Operation = "write",
                Error = ex.Message,
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
    }

    public async Task<ResourceResult> Pipe(string pipeline)
    {
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var parts = pipeline.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return new ResourceResult
                {
                    Path = pipeline,
                    Operation = "pipe",
                    Error = "Pipeline requires at least 2 commands separated by |",
                    LatencyMs = sw.ElapsedMilliseconds
                };

            ResourceResult? current = null;

            for (int i = 0; i < parts.Length; i++)
            {
                var trimmed = parts[i].Trim();
                ResourceResult result;

                if (trimmed.StartsWith("read ", StringComparison.OrdinalIgnoreCase))
                {
                    var path = trimmed[5..].Trim();
                    result = await Read(path).ConfigureAwait(false);
                }
                else if (trimmed.StartsWith("search ", StringComparison.OrdinalIgnoreCase))
                {
                    var searchArgs = trimmed[7..].Trim();
                    var match = s_searchArgsRegex.Match(searchArgs);
                    var path = match.Success ? match.Groups[1].Value.Trim() : "/";
                    var query = match.Success ? match.Groups[2].Value.Trim() : searchArgs;
                    result = await Search(path, query).ConfigureAwait(false);
                }
                else if (trimmed.StartsWith("list", StringComparison.OrdinalIgnoreCase))
                {
                    var path = trimmed.Length > 4 ? trimmed[4..].Trim() : "/";
                    result = await List(path).ConfigureAwait(false);
                }
                else if (trimmed.StartsWith("write ", StringComparison.OrdinalIgnoreCase))
                {
                    var writeArgs = trimmed[6..].Trim();
                    var match = s_writeArgsRegex.Match(writeArgs);
                    var path = match.Success ? match.Groups[1].Value.Trim() : writeArgs;
                    var content = match.Success ? match.Groups[2].Value.Trim() : "";
                    result = await Write(path, content).ConfigureAwait(false);
                }
                else
                {
                    return new ResourceResult
                    {
                        Path = pipeline,
                        Operation = "pipe",
                        Error = $"Unknown pipeline command: {trimmed}",
                        LatencyMs = sw.ElapsedMilliseconds
                    };
                }

                if (result.Error != null)
                    return new ResourceResult
                    {
                        Path = pipeline,
                        Operation = "pipe",
                        Error = $"Stage {i} failed: {result.Error}",
                        LatencyMs = sw.ElapsedMilliseconds
                    };

                current = result;
            }

            return current ?? new ResourceResult
            {
                Path = pipeline,
                Operation = "pipe",
                Error = "Pipeline produced no result",
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipe failed for pipeline: {Pipeline}", pipeline);
            return new ResourceResult
            {
                Path = pipeline,
                Operation = "pipe",
                Error = ex.Message,
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
    }

    public string Snapshot(string label = "")
    {
        if (string.IsNullOrEmpty(label))
            label = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

        var state = new Dictionary<string, MountPoint>(_mounts);
        _snapshots[label] = state;
        _logger.LogInformation("Snapshot created: {Label} ({Count} mounts)", label, state.Count);
        return label;
    }

    public bool Restore(string label)
    {
        if (!_snapshots.TryGetValue(label, out var state))
        {
            _logger.LogWarning("Snapshot not found: {Label}", label);
            return false;
        }

        _mounts.Clear();
        foreach (var kvp in state)
            _mounts[kvp.Key] = kvp.Value;

        _logger.LogInformation("Snapshot restored: {Label} ({Count} mounts)", label, state.Count);
        return true;
    }

    public List<string> ListSnapshots()
    {
        return _snapshots.Keys.OrderBy(k => k).ToList();
    }

    public Dictionary<string, object> Stats()
    {
        return new Dictionary<string, object>
        {
            ["mountCount"] = _mounts.Count,
            ["snapshotCount"] = _snapshots.Count,
            ["mountPaths"] = _mounts.Keys.OrderBy(k => k).ToList()
        };
    }

    public static ResourceTree CreateLivingTreeFS()
    {
        var tree = Instance;

        tree.Mount("/knowledge", "knowledge",
            readFn: async path =>
            {
                await Task.Delay(1).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { source = "knowledge_base", path, cached = true });
            },
            listFn: async () =>
            {
                await Task.Delay(1).ConfigureAwait(false);
                return new List<object>
                {
                    new { name = "ai_basics.md", type = "doc" },
                    new { name = "best_practices.md", type = "doc" },
                    new { name = "reference.json", type = "data" }
                };
            },
            searchFn: async (query, topK) =>
            {
                await Task.Delay(5).ConfigureAwait(false);
                return new List<object>
                {
                    new { score = 0.95, title = $"Knowledge result for: {query}", snippet = "Placeholder knowledge content..." }
                }.Take(topK).Cast<object>().ToList();
            });

        tree.Mount("/weather", "weather",
            readFn: async path =>
            {
                await Task.Delay(20).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    location = path.Trim('/'),
                    temperature = 22.5,
                    humidity = 65,
                    condition = "partly_cloudy",
                    timestamp = DateTime.UtcNow.ToString("O")
                });
            },
            searchFn: async (query, topK) =>
            {
                await Task.Delay(10).ConfigureAwait(false);
                return new List<object>
                {
                    new { location = query, forecast = "sunny", temp_high = 28, temp_low = 18 }
                };
            });

        tree.Mount("/models", "models",
            listFn: async () =>
            {
                await Task.Delay(2).ConfigureAwait(false);
                return new List<object>
                {
                    new { provider = "openai", model = "gpt-4o", status = "available" },
                    new { provider = "deepseek", model = "deepseek-v4", status = "available" },
                    new { provider = "local", model = "phi-4", status = "ready" }
                };
            },
            searchFn: async (query, topK) =>
            {
                await Task.Delay(3).ConfigureAwait(false);
                return new List<object>
                {
                    new { provider = "openai", model = "gpt-4o", capability = query, score = 0.98 }
                };
            });

        tree.Mount("/graph", "graph",
            readFn: async path =>
            {
                await Task.Delay(10).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    node = path.Trim('/'),
                    edges = new[] { new { to = "concept_B", weight = 0.8 }, new { to = "concept_C", weight = 0.6 } },
                    depth = 1
                });
            },
            searchFn: async (query, topK) =>
            {
                await Task.Delay(15).ConfigureAwait(false);
                return new List<object>
                {
                    new { node = query, rank = 1, embedding_distance = 0.12 },
                    new { node = $"{query}_related", rank = 2, embedding_distance = 0.34 }
                };
            });

        tree.Mount("/session", "session",
            readFn: async path =>
            {
                await Task.Delay(1).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    session_id = path.Trim('/'),
                    messages = 12,
                    tokens_used = 4500,
                    started = DateTime.UtcNow.AddHours(-1).ToString("O")
                });
            },
            writeFn: async (path, content) =>
            {
                await Task.Delay(5).ConfigureAwait(false);
                _ = content;
                return true;
            });

        tree._logger.LogInformation("LivingTreeFS initialized with {Count} mounts", tree._mounts.Count);
        return tree;
    }
}
