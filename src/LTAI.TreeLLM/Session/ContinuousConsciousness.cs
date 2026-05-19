using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using LTAI.TreeLLM.Models;

namespace LTAI.TreeLLM.Session;

public sealed class ContinuousConsciousness
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<string, MemoryBlock> _memoryBlocks = new();
    private readonly List<Dictionary<string, string>> _activeContext = new();
    private readonly HashSet<string> _recentTopics = new();
    private readonly ILogger<ContinuousConsciousness>? _logger;
    private readonly string _persistPath;
    private int _taskCount;
    private int _messageCounter;
    private string _currentTaskType = "general";
    private DateTime _lastSave = DateTime.UtcNow;

    private const int MAX_ACTIVE_CONTEXT = 50;
    private const int COMPACT_KEEP_RECENT = 25;
    private const int SAVE_EVERY_MESSAGES = 10;

    public ContinuousConsciousness(ILogger<ContinuousConsciousness>? logger = null, string? persistPath = null)
    {
        _logger = logger;
        _persistPath = persistPath ?? Path.Combine("livingtree", "meta", "continuous_consciousness.json");
        Load();
    }

    public Dictionary<string, object> OnMessage(string message, string taskType = "general")
    {
        _messageCounter++;
        _currentTaskType = taskType;

        var boundary = DetectTaskBoundary(message);
        if (boundary != null)
        {
            ArchiveTask(boundary);
            _taskCount++;
        }

        _activeContext.Add(new Dictionary<string, string>
        {
            ["role"] = "user",
            ["content"] = message
        });

        UpdateTopics(message);

        if (_activeContext.Count > MAX_ACTIVE_CONTEXT)
            CompactActiveContext();

        var memories = RetrieveRelevantMemories(message, 3);
        var enrichedContext = BuildEnrichedContext(message, memories);

        if (_messageCounter % SAVE_EVERY_MESSAGES == 0)
            Save();

        return new Dictionary<string, object>
        {
            ["boundary_detected"] = boundary != null,
            ["active_context_size"] = _activeContext.Count,
            ["memory_blocks"] = _memoryBlocks.Count,
            ["relevant_memories"] = memories.Count,
            ["task_type"] = taskType
        };
    }

    public void OnResponse(string responseText, bool success)
    {
        _activeContext.Add(new Dictionary<string, string>
        {
            ["role"] = "assistant",
            ["content"] = responseText
        });

        var topics = ExtractTopics(responseText);
        foreach (var t in topics) _recentTopics.Add(t);

        if (_messageCounter % SAVE_EVERY_MESSAGES == 0)
            Save();
    }

    private TaskBoundary? DetectTaskBoundary(string message)
    {
        var text = message.ToLower();

        if (text.Contains("thank you") || text.Contains("thanks") ||
            text.Contains("goodbye") || text.Contains("bye") ||
            text.Contains("that's all") || text.Contains("got it") ||
            text.Contains("i'm done") || text.Contains("done for now"))
        {
            return new TaskBoundary
            {
                Reason = "closing_phrase",
                Confidence = 0.9,
                ContextSnapshot = SummarizeContext(),
                KeyPoints = ExtractKeyPoints()
            };
        }

        if (_activeContext.Count >= MAX_ACTIVE_CONTEXT - 5)
        {
            return new TaskBoundary
            {
                Reason = "context_full",
                Confidence = 0.7,
                ContextSnapshot = SummarizeContext(),
                KeyPoints = ExtractKeyPoints()
            };
        }

        return null;
    }

    private void ArchiveTask(TaskBoundary boundary)
    {
        var compressed = SummarizeContext();
        var decisions = ExtractDecisions(_activeContext);

        var block = new MemoryBlock
        {
            Id = $"mem_{Guid.NewGuid():N}"[..12],
            Content = string.Join("\n", decisions.Count > 0 ? decisions : new[] { compressed }),
            OriginalLength = _activeContext.Sum(m => m["content"].Length),
            TaskType = _currentTaskType,
            Topics = _recentTopics.ToList()
        };

        _memoryBlocks[block.Id] = block;

        _activeContext.Clear();
        _recentTopics.Clear();

        _logger?.LogDebug("ContinuousConsciousness: Archived task with {DecisionCount} decisions",
            decisions.Count);
    }

    private void CompactActiveContext()
    {
        var keepRecent = _activeContext.Skip(_activeContext.Count - COMPACT_KEEP_RECENT).ToList();
        var older = _activeContext.Take(_activeContext.Count - COMPACT_KEEP_RECENT).ToList();

        var summary = SummarizeMessages(older);

        _activeContext.Clear();
        _activeContext.Add(new Dictionary<string, string>
        {
            ["role"] = "system",
            ["content"] = $"[Compressed context from earlier in session: {summary}]"
        });
        _activeContext.AddRange(keepRecent);
    }

    private List<MemoryBlock> RetrieveRelevantMemories(string query, int topK)
    {
        var queryWords = Tokenize(query);
        if (queryWords.Count == 0) return new List<MemoryBlock>();

        var scored = new List<(MemoryBlock Block, double Score)>();

        foreach (var block in _memoryBlocks.Values)
        {
            var blockWords = Tokenize(block.Content);
            var overlap = queryWords.Intersect(blockWords).Count();
            var jaccard = (double)overlap / (queryWords.Count + blockWords.Count - overlap);

            var hoursAgo = (DateTime.UtcNow - block.Timestamp).TotalHours;
            var temporalScore = Math.Exp(-hoursAgo / 24.0);

            var taskMatch = block.TaskType == _currentTaskType ? 0.3 : 0.0;

            var score = jaccard * 0.4 + temporalScore * 0.3 + taskMatch;

            if (score > 0.15)
            {
                block.LastAccessed = DateTime.UtcNow;
                block.AccessCount++;
                scored.Add((block, score));
            }
        }

        return scored.OrderByDescending(s => s.Score).Take(topK).Select(s => s.Block).ToList();
    }

    private string BuildEnrichedContext(string message, List<MemoryBlock> memories)
    {
        if (memories.Count == 0) return message;

        var parts = memories.Select(m =>
        {
            var daysAgo = (DateTime.UtcNow - m.Timestamp).Days;
            var snippet = m.Content.Length > 200 ? m.Content[..200] + "..." : m.Content;
            return $"[{daysAgo}d ago] {snippet}";
        });

        return string.Join("\n", parts) + "\n---\n" + message;
    }

    private string SummarizeContext()
    {
        var lastMessages = _activeContext.TakeLast(10)
            .Where(m => m.TryGetValue("role", out var r) && (r == "user" || r == "assistant"))
            .Select(m => m["content"])
            .ToList();

        var text = string.Join("\n", lastMessages);
        return text.Length > 2000 ? text[..2000] + "..." : text;
    }

    private string SummarizeMessages(List<Dictionary<string, string>> messages)
    {
        var userMsgs = messages.Where(m => m.TryGetValue("role", out var r) && r == "user")
            .Select(m => m["content"]).ToList();
        var text = string.Join(" | ", userMsgs);
        return text.Length > 500 ? text[..500] + "..." : text;
    }

    private List<string> ExtractDecisions(List<Dictionary<string, string>> messages)
    {
        var keywords = new[] { "decided to", "will use", "plan is", "using", "choose", "approach",
            "决定", "使用", "方案", "选择", "采用", "确定" };
        var decisions = new List<string>();

        foreach (var msg in messages)
        {
            if (msg.TryGetValue("role", out var r) && r == "assistant")
            {
                foreach (var kw in keywords)
                {
                    var idx = msg["content"].IndexOf(kw, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        var snippet = msg["content"][Math.Max(0, idx)..];
                        if (snippet.Length > 150) snippet = snippet[..150];
                        decisions.Add(snippet);
                        break;
                    }
                }
            }
        }

        return decisions;
    }

    private void UpdateTopics(string message)
    {
        var keywords = ExtractTopics(message);
        foreach (var k in keywords) _recentTopics.Add(k);
    }

    private static List<string> ExtractTopics(string text)
    {
        var keywords = new[] { "code", "api", "database", "config", "deploy", "test", "debug",
            "build", "docker", "git", "security", "performance", "network", "ui", "docs" };
        var found = new List<string>();
        var lower = text.ToLower();
        foreach (var kw in keywords)
            if (lower.Contains(kw)) found.Add(kw);
        return found;
    }

    private List<string> ExtractKeyPoints()
    {
        var lastMsgs = _activeContext.TakeLast(5);
        var points = new List<string>();
        foreach (var m in lastMsgs)
        {
            if (m.TryGetValue("content", out var c))
            {
                var trimmed = c.Length > 100 ? c[..100] : c;
                points.Add(trimmed);
            }
        }
        return points;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var words = text.ToLower()
            .Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '(', ')', '[', ']', '{', '}' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .ToHashSet();
        return words;
    }

    private void Save()
    {
        try
        {
            _lastSave = DateTime.UtcNow;
            var dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new
            {
                memory_blocks = _memoryBlocks.Values.ToList(),
                recent_topics = _recentTopics.ToList(),
                task_count = _taskCount,
                current_task_type = _currentTaskType,
                saved_at = DateTime.UtcNow.ToString("O")
            };

            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(_persistPath, json);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("ContinuousConsciousness: Save failed: {Message}", ex.Message);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_persistPath)) return;

            var json = File.ReadAllText(_persistPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (data == null) return;

            if (data.TryGetValue("memory_blocks", out var blocks))
            {
                var loaded = JsonSerializer.Deserialize<List<MemoryBlock>>(blocks.GetRawText());
                if (loaded != null)
                    foreach (var block in loaded)
                        _memoryBlocks[block.Id] = block;
            }

            if (data.TryGetValue("recent_topics", out var topics))
            {
                var loaded = JsonSerializer.Deserialize<List<string>>(topics.GetRawText());
                if (loaded != null)
                    foreach (var t in loaded) _recentTopics.Add(t);
            }

            if (data.TryGetValue("task_count", out var tc)) _taskCount = tc.GetInt32();
            if (data.TryGetValue("current_task_type", out var ctt))
                _currentTaskType = ctt.GetString() ?? "general";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("ContinuousConsciousness: Load failed: {Message}", ex.Message);
        }
    }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["memory_blocks"] = _memoryBlocks.Count,
            ["active_context_size"] = _activeContext.Count,
            ["recent_topics"] = _recentTopics.Count,
            ["task_count"] = _taskCount,
            ["current_task_type"] = _currentTaskType
        };
    }
}
