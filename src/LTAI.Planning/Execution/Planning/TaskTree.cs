using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTAI.Core.Interfaces;
using LTAI.Planning.TaskModels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TaskStatus = LTAI.Planning.TaskModels.TaskStatus;

namespace LTAI.Planning.TaskModels
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum TaskStatus
    {
        [JsonPropertyName("pending")]
        Pending,

        [JsonPropertyName("thinking")]
        Thinking,

        [JsonPropertyName("running")]
        Running,

        [JsonPropertyName("done")]
        Done,

        [JsonPropertyName("failed")]
        Failed,

        [JsonPropertyName("skipped")]
        Skipped
    }

    public class TaskNode
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("status")]
        public TaskStatus Status { get; set; } = TaskStatus.Pending;

        [JsonPropertyName("parent_id")]
        public string? ParentId { get; set; }

        [JsonPropertyName("children")]
        [JsonIgnore]
        public List<TaskNode> Children { get; set; } = new();

        [JsonPropertyName("depth")]
        public int Depth { get; set; }

        [JsonPropertyName("priority")]
        public string Priority { get; set; } = "P2";

        [JsonPropertyName("estimated_tokens")]
        public int EstimatedTokens { get; set; }

        [JsonPropertyName("actual_tokens")]
        public int ActualTokens { get; set; }

        [JsonPropertyName("reasoning")]
        public string Reasoning { get; set; } = "";

        [JsonPropertyName("dependencies")]
        public List<string> Dependencies { get; set; } = new();

        [JsonPropertyName("result")]
        public string? Result { get; set; }

        [JsonPropertyName("created_at")]
        public double CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        [JsonPropertyName("started_at")]
        public double? StartedAt { get; set; }

        [JsonPropertyName("completed_at")]
        public double? CompletedAt { get; set; }

        public Dictionary<string, object?> ToDict()
        {
            return new()
            {
                ["id"] = Id,
                ["label"] = Label,
                ["description"] = Description,
                ["status"] = Status.ToString().ToLowerInvariant(),
                ["parent_id"] = ParentId,
                ["depth"] = Depth,
                ["priority"] = Priority,
                ["estimated_tokens"] = EstimatedTokens,
                ["actual_tokens"] = ActualTokens,
                ["reasoning"] = Reasoning,
                ["dependencies"] = Dependencies,
                ["result"] = Result,
                ["created_at"] = CreatedAt,
                ["started_at"] = StartedAt,
                ["completed_at"] = CompletedAt
            };
        }

        public Dictionary<string, object?> ToTreeDict()
        {
            var data = ToDict();
            data["children"] = Children.Select(c => c.ToTreeDict()).ToList();
            return data;
        }

        public double Progress()
        {
            if (Children.Count == 0)
            {
                return Status switch
                {
                    TaskStatus.Done => 1.0,
                    TaskStatus.Running => 0.5,
                    TaskStatus.Thinking => 0.2,
                    _ => 0.0
                };
            }

            double total = 0;
            foreach (var child in Children)
            {
                total += child.Status switch
                {
                    TaskStatus.Done => 1.0,
                    TaskStatus.Running => 0.5,
                    TaskStatus.Thinking => 0.2,
                    _ => 0.0
                };
            }
            return total / Children.Count;
        }
    }
}

namespace LTAI.Planning.Planning
{
    public class TaskTree
    {
        private TaskNode? _root;
        private readonly Dictionary<string, TaskNode> _nodeIndex = new();
        private readonly List<Dictionary<string, object?>> _eventLog = new();
        private readonly ILogger<TaskTree> _logger;

        public TaskNode? Root => _root;

        public TaskTree(ILogger<TaskTree> logger)
        {
            _logger = logger;
        }

        public TaskNode CreateRoot(string description)
        {
            if (_root is not null)
                _logger.LogWarning("TaskTree already has a root; overwriting");

            _root = new TaskNode
            {
                Label = description.Length > 60 ? description[..60] : description,
                Description = description,
                Status = TaskStatus.Thinking,
                Depth = 0,
                Priority = "P0"
            };

            _nodeIndex[_root.Id] = _root;
            LogEvent("node_update", _root);
            _logger.LogInformation("TaskTree root created | id={Id} description={Desc}",
                _root.Id, description[..Math.Min(description.Length, 60)]);
            return _root;
        }

        public TaskNode AddChild(
            string parentId,
            string label,
            string description = "",
            string priority = "P2",
            int estimatedTokens = 0,
            string reasoning = "",
            List<string>? dependencies = null)
        {
            if (!_nodeIndex.TryGetValue(parentId, out var parent))
                throw new KeyNotFoundException($"Parent node {parentId} not found in index");

            var child = new TaskNode
            {
                Label = label,
                Description = description,
                ParentId = parentId,
                Depth = parent.Depth + 1,
                Priority = priority,
                EstimatedTokens = estimatedTokens,
                Reasoning = reasoning,
                Dependencies = dependencies ?? new()
            };

            parent.Children.Add(child);
            _nodeIndex[child.Id] = child;
            LogEvent("node_update", child);
            _logger.LogDebug("Child added | id={Id} parent={ParentId} label={Label}", child.Id, parentId, label);
            return child;
        }

        public TaskNode UpdateStatus(string nodeId, TaskStatus status, string reasoning = "")
        {
            if (!_nodeIndex.TryGetValue(nodeId, out var node) || node is null)
                throw new KeyNotFoundException($"Node {nodeId} not found in index");
            var oldStatus = node.Status;
            node.Status = status;

            if (!string.IsNullOrEmpty(reasoning))
                node.Reasoning = reasoning;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (status == TaskStatus.Running && oldStatus != TaskStatus.Running)
                node.StartedAt = now;
            else if (status is TaskStatus.Done or TaskStatus.Failed or TaskStatus.Skipped)
                node.CompletedAt = now;

            if (status == TaskStatus.Failed)
            {
                foreach (var (id, candidate) in _nodeIndex)
                {
                    if ((candidate.ParentId == nodeId || candidate.Dependencies.Contains(nodeId))
                        && candidate.Status == TaskStatus.Pending)
                    {
                        UpdateStatus(candidate.Id, TaskStatus.Skipped,
                            $"Parent/dependency {nodeId} failed");
                    }
                }
            }

            LogEvent("node_update", node);
            _logger.LogDebug("Status transition | id={Id} {Old}->{New}", nodeId, oldStatus, status);
            return node;
        }

        public TaskNode SetResult(string nodeId, string resultText)
        {
            if (!_nodeIndex.TryGetValue(nodeId, out var node) || node is null)
                throw new KeyNotFoundException($"Node {nodeId} not found in index");
            node.Result = resultText;

            if (node.Status is TaskStatus.Running or TaskStatus.Pending or TaskStatus.Thinking)
                UpdateStatus(nodeId, TaskStatus.Done);
            else
                LogEvent("node_update", node);

            return node;
        }

        public TaskNode? GetTree() => _root;

        public TaskNode GetNode(string nodeId) =>
            _nodeIndex.TryGetValue(nodeId, out var node) && node is not null
                ? node
                : throw new KeyNotFoundException($"Node {nodeId} not found in index");

        public Dictionary<string, object?> Stats()
        {
            var nodes = _nodeIndex.Values.ToList();
            if (nodes.Count == 0)
            {
                return new()
                {
                    ["total"] = 0,
                    ["by_status"] = new Dictionary<string, int>(),
                    ["max_depth"] = 0
                };
            }

            var byStatus = new Dictionary<string, int>();
            foreach (var node in nodes)
            {
                var key = node.Status.ToString().ToLowerInvariant();
                byStatus[key] = byStatus.GetValueOrDefault(key) + 1;
            }

            return new()
            {
                ["total"] = nodes.Count,
                ["by_status"] = byStatus,
                ["max_depth"] = nodes.Max(n => n.Depth),
                ["progress"] = _root?.Progress() ?? 0.0
            };
        }

        public string ToSseEvents()
        {
            if (_root is null)
            {
                _logger.LogWarning("to_sse_events called on empty tree");
                return "";
            }

            var sb = new StringBuilder();

            sb.Append(FormatSse("task_init", new Dictionary<string, object?>
            {
                ["tree"] = _root.ToTreeDict(),
                ["stats"] = Stats()
            }));

            foreach (var entry in _eventLog)
            {
                if (entry.TryGetValue("event", out var evt) && evt?.ToString() == "node_update"
                    && entry.TryGetValue("data", out var data))
                {
                    sb.Append(FormatSse("node_update", (Dictionary<string, object?>?)data));
                }
            }

            sb.Append(FormatSse("task_done", new Dictionary<string, object?>
            {
                ["summary"] = Stats(),
                ["root_id"] = _root.Id
            }));

            return sb.ToString();
        }

        private void LogEvent(string eventType, TaskNode node)
        {
            _eventLog.Add(new()
            {
                ["event"] = eventType,
                ["data"] = node.ToDict(),
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }

        internal static string FormatSse(string eventType, Dictionary<string, object?>? data)
        {
            var json = JsonSerializer.Serialize(data ?? new(), new JsonSerializerOptions
            {
                WriteIndented = false
            });
            return $"event: {eventType}\ndata: {json}\n\n";
        }
    }

    public class TaskDecomposer
    {
        internal static readonly Dictionary<string, List<Dictionary<string, object?>>> PatternLibrary = new()
        {
            ["authentication"] = new()
            {
                new() { ["label"] = "User Model & Storage", ["priority"] = "P0", ["reasoning"] = "Core identity persistence" },
                new() { ["label"] = "Credential Validation", ["priority"] = "P0", ["reasoning"] = "Password hashing and comparison" },
                new() { ["label"] = "Token / Session Management", ["priority"] = "P1", ["reasoning"] = "JWT or session cookie issuance" },
                new() { ["label"] = "OAuth / Social Login", ["priority"] = "P2", ["reasoning"] = "Third-party identity provider integration" },
                new() { ["label"] = "Rate Limiting & Lockout", ["priority"] = "P1", ["reasoning"] = "Brute-force protection" }
            },
            ["crud"] = new()
            {
                new() { ["label"] = "Data Model & Schema", ["priority"] = "P0", ["reasoning"] = "DB schema or ORM definitions" },
                new() { ["label"] = "Create Endpoint", ["priority"] = "P0", ["reasoning"] = "POST handler with validation" },
                new() { ["label"] = "Read / List Endpoint", ["priority"] = "P0", ["reasoning"] = "GET with filtering and pagination" },
                new() { ["label"] = "Update Endpoint", ["priority"] = "P1", ["reasoning"] = "PUT/PATCH handler" },
                new() { ["label"] = "Delete Endpoint", ["priority"] = "P1", ["reasoning"] = "Soft or hard delete logic" }
            },
            ["pipeline"] = new()
            {
                new() { ["label"] = "Data Ingestion", ["priority"] = "P0", ["reasoning"] = "Input source connectors" },
                new() { ["label"] = "Validation & Cleaning", ["priority"] = "P0", ["reasoning"] = "Schema enforcement and sanitization" },
                new() { ["label"] = "Transformation", ["priority"] = "P1", ["reasoning"] = "Business logic and mapping" },
                new() { ["label"] = "Storage / Persistence", ["priority"] = "P1", ["reasoning"] = "Output sink" },
                new() { ["label"] = "Monitoring & Alerts", ["priority"] = "P2", ["reasoning"] = "Observability hooks" }
            }
        };

        private static readonly JsonSerializerOptions SseJsonOptions = new()
        {
            WriteIndented = false
        };

        private object? _consciousness;
        private readonly ILogger<TaskDecomposer> _logger;
        private readonly int _maxDepth;
        private readonly int _maxChildren;

        public TaskDecomposer(
            ILogger<TaskDecomposer> logger,
            object? consciousness = null,
            int maxDepth = 4,
            int maxChildren = 6)
        {
            _logger = logger;
            _consciousness = consciousness;
            _maxDepth = maxDepth;
            _maxChildren = maxChildren;
        }

        public void SetConsciousness(object c)
        {
            _consciousness = c;
        }

        public async IAsyncEnumerable<string> Decompose(
            string description,
            int? maxDepth = null,
            int? maxChildren = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var depthLimit = maxDepth ?? _maxDepth;
            var childLimit = maxChildren ?? _maxChildren;

            _logger.LogInformation(
                "TaskDecomposer.Decompose start | desc={Desc} max_depth={Depth} max_children={Children}",
                description[..Math.Min(description.Length, 60)], depthLimit, childLimit);

            var treeLogger = new LoggerFactory().CreateLogger<TaskTree>();
            var tree = new TaskTree(treeLogger);
            var root = tree.CreateRoot(description);

            yield return FormatNodeSse("task_init", new Dictionary<string, object?>
            {
                ["tree"] = root.ToTreeDict(),
                ["stats"] = tree.Stats()
            });

            var pattern = MatchPattern(description);
            if (pattern is not null)
            {
                foreach (var entry in pattern)
                {
                    var child = tree.AddChild(
                        parentId: root.Id,
                        label: entry.GetValueOrDefault("label")?.ToString() ?? "Sub-task",
                        description: description,
                        priority: entry.GetValueOrDefault("priority")?.ToString() ?? "P2",
                        reasoning: entry.GetValueOrDefault("reasoning")?.ToString() ?? ""
                    );
                    yield return FormatNodeSse("node_update", child.ToDict());
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            else
            {
                await DecomposeLevel(tree, root, depthLimit, childLimit, cancellationToken);
            }

            var queue = new Queue<TaskNode>(root.Children);
            while (queue.Count > 0 && !cancellationToken.IsCancellationRequested)
            {
                var node = queue.Dequeue();
                if (node.Depth < depthLimit - 1)
                {
                    tree.UpdateStatus(node.Id, TaskStatus.Thinking);
                    yield return FormatNodeSse("node_update", node.ToDict());
                    await DecomposeLevel(tree, node, depthLimit, childLimit, cancellationToken);
                }

                foreach (var child in node.Children)
                    queue.Enqueue(child);
            }

            yield return FormatNodeSse("task_done", new Dictionary<string, object?>
            {
                ["summary"] = tree.Stats(),
                ["root_id"] = root.Id
            });
        }

        private async Task DecomposeLevel(
            TaskTree tree,
            TaskNode parent,
            int depthLimit,
            int childLimit,
            CancellationToken cancellationToken)
        {
            if (parent.Depth >= depthLimit)
                return;

            var prompt = BuildDecompositionPrompt(parent, childLimit);
            var response = await LlmGenerate(prompt, cancellationToken);
            var subtasks = ParseSubtasks(response);

            foreach (var sub in subtasks.Take(childLimit))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var child = tree.AddChild(
                    parentId: parent.Id,
                    label: sub.GetValueOrDefault("label")?.ToString() ?? "Sub-task",
                    description: sub.GetValueOrDefault("description")?.ToString() ?? parent.Description,
                    priority: sub.GetValueOrDefault("priority")?.ToString() ?? "P2",
                    estimatedTokens: sub.TryGetValue("estimated_tokens", out var est) && est is int e ? e : 0,
                    reasoning: sub.GetValueOrDefault("reasoning")?.ToString() ?? "",
                    dependencies: sub.TryGetValue("dependencies", out var deps) && deps is List<string> d ? d : null
                );

                _logger.LogDebug("LLM-generated child | id={Id} label={Label}", child.Id, child.Label);
            }
        }

        private static string BuildDecompositionPrompt(TaskNode node, int maxChildren)
        {
            return $"Break down the following task into {maxChildren} or fewer sub-tasks. " +
                   "Consider sequential dependencies when ordering. " +
                   "Return a JSON array of objects, each with keys: " +
                   "label (short name), description (one sentence), priority (P0/P1/P2/P3), " +
                   "reasoning (why this sub-task), dependencies (list of 0-based sibling indices that must complete first), " +
                   "estimated_tokens (integer). " +
                   $"\n\nTask (depth {node.Depth}): {node.Description}\n\nJSON:";
        }

        public static List<Dictionary<string, object?>>? MatchPattern(string description)
        {
            var lower = description.ToLowerInvariant();
            foreach (var (keyword, pattern) in PatternLibrary)
            {
                if (lower.Contains(keyword))
                {
                    return pattern;
                }
            }
            return null;
        }

        private async Task<string> LlmGenerate(string prompt, CancellationToken cancellationToken)
        {
            if (_consciousness is null)
            {
                _logger.LogWarning("No consciousness reference; using fallback decomposition");
                return "[]";
            }

            try
            {
                if (_consciousness is IChatClient engine)
                    return await engine.CompleteAsync(prompt, cancellationToken: cancellationToken);

                if (_consciousness is Func<string, Task<string>> asyncFunc)
                    return await asyncFunc(prompt);

                if (_consciousness is Func<string, string> syncFunc)
                    return syncFunc(prompt);

                var type = _consciousness.GetType();
                var generateMethod = type.GetMethod("generate", new[] { typeof(string) })
                                  ?? type.GetMethod("Generate", new[] { typeof(string) });
                if (generateMethod is not null)
                {
                    var result = generateMethod.Invoke(_consciousness, new object[] { prompt });
                    if (result is Task<string> taskResult)
                        return await taskResult;
                    if (result is string strResult)
                        return strResult;
                    return result?.ToString() ?? "[]";
                }

                var thinkMethod = type.GetMethod("think", new[] { typeof(string) })
                               ?? type.GetMethod("Think", new[] { typeof(string) });
                if (thinkMethod is not null)
                {
                    var result = thinkMethod.Invoke(_consciousness, new object[] { prompt });
                    if (result is Task<string> taskResult)
                        return await taskResult;
                    if (result is string strResult)
                        return strResult;
                    return result?.ToString() ?? "[]";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LLM generation failed");
            }

            return "[]";
        }

        public static List<Dictionary<string, object?>> ParseSubtasks(string raw)
        {
            var text = raw.Trim();

            if (text.StartsWith("```"))
            {
                var lines = text.Split('\n').ToList();
                if (lines.Count > 0 && lines[0].StartsWith("```"))
                    lines.RemoveAt(0);
                if (lines.Count > 0 && lines[^1].StartsWith("```"))
                    lines.RemoveAt(lines.Count - 1);
                text = string.Join("\n", lines).Trim();
            }

            var start = text.IndexOf('[');
            var end = text.LastIndexOf(']');
            if (start == -1 || end == -1)
                return new();

            try
            {
                var json = text[start..(end + 1)];
                var parsed = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json);
                if (parsed is null)
                    return new();

                var result = new List<Dictionary<string, object?>>();
                foreach (var entry in parsed)
                {
                    var normalized = new Dictionary<string, object?>();
                    foreach (var (key, element) in entry)
                    {
                        normalized[key] = element.ValueKind switch
                        {
                            JsonValueKind.String => element.GetString(),
                            JsonValueKind.Number => element.TryGetInt32(out var intVal) ? intVal : element.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Array => element.EnumerateArray()
                                .Select(e => e.GetString() ?? e.ToString())
                                .ToList<string>(),
                            JsonValueKind.Null => null,
                            _ => element.ToString()
                        };
                    }
                    result.Add(normalized);
                }
                return result;
            }
            catch (JsonException)
            {
                return new();
            }
        }

        internal static string FormatNodeSse(string eventType, Dictionary<string, object?>? data)
        {
            var json = JsonSerializer.Serialize(data ?? new(), SseJsonOptions);
            return $"event: {eventType}\ndata: {json}\n\n";
        }
    }

    public static class TaskPlanning
    {
        private static TaskDecomposer? _decomposerInstance;
        private static readonly Lock DecomposerLock = new();

        public static List<Dictionary<string, object?>>? MatchPattern(string description)
            => TaskDecomposer.MatchPattern(description);

        public static TaskDecomposer GetTaskDecomposer(
            ILogger<TaskDecomposer> logger,
            object? consciousness = null,
            int? maxDepth = null,
            int? maxChildren = null,
            bool forceReset = false)
        {
            if (forceReset || _decomposerInstance is null)
            {
                lock (DecomposerLock)
                {
                    if (forceReset || _decomposerInstance is null)
                    {
                        _decomposerInstance = new TaskDecomposer(
                            logger,
                            consciousness: consciousness,
                            maxDepth: maxDepth ?? 4,
                            maxChildren: maxChildren ?? 6
                        );
                    }
                }
            }
            return _decomposerInstance;
        }

        public static void ResetDecomposer()
        {
            lock (DecomposerLock)
            {
                _decomposerInstance = null;
            }
        }

        public static TaskTree CreateTaskTree(ILogger<TaskTree> logger, string description)
        {
            var tree = new TaskTree(logger);
            tree.CreateRoot(description);
            return tree;
        }
    }
}
