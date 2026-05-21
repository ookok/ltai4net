using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using LTAI.Core.Utility;
using LTAI.TreeLLM.Models;

namespace LTAI.TreeLLM.Session;

public sealed class CrossSessionBridge
{
    public record ChatMessage(string Role, string Content);

    private static readonly Regex DecisionKeywords = new(
        @"决定|结论|方案|decided|decision|conclusion|plan|选定|确定|最终选择",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PreferenceKeywords = new(
        @"偏好|喜欢|习惯|prefer|like|favorite|常用|首选|default",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PendingKeywords = new(
        @"待办|后续|继续|pending|todo|follow.up|下次|稍后|未完|later",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<string, List<MemoryEntry>> _memories = new();
    private readonly object _lock = new();
    private readonly ILogger<CrossSessionBridge>? _logger;
    private readonly string _persistPath;
    private readonly TimeSpan _ttl = TimeSpan.FromDays(7);
    private const int MaxPerUser = 30;

    public CrossSessionBridge(ILogger<CrossSessionBridge>? logger = null, string? persistPath = null)
    {
        _logger = logger;
        _persistPath = persistPath ?? Path.Combine("livingtree", "meta", "cross_session_memories.json");
        Load();
    }

    public int ExtractMemories(string userId, List<ChatMessage> messages)
    {
        if (string.IsNullOrEmpty(userId) || messages == null || messages.Count == 0)
            return 0;

        var count = 0;
        var recent = messages.Skip(Math.Max(0, messages.Count - 15)).ToList();
        var memories = _memories.GetOrAdd(userId, _ => new List<MemoryEntry>());

        foreach (var msg in recent)
        {
            var type = DetermineMemoryType(msg.Content);
            if (type == null) continue;

            var snippet = ExtractSnippet(msg.Content, 150, 200);
            if (string.IsNullOrEmpty(snippet)) continue;

            lock (_lock)
            {
                if (memories.Any(m => m.Text == snippet && m.Type == type))
                    continue;

                memories.Add(new MemoryEntry
                {
                    Type = type,
                    Text = snippet,
                    Timestamp = DateTime.UtcNow
                });
                count++;
            }
        }

        PruneMemories(userId);
        Save();

        _logger?.LogDebug("Extracted {Count} memories for user {UserId}", count, userId);
        return count;
    }

    public string InjectWeightedContext(string userId, string currentQuery, int maxBudget = 400)
    {
        if (string.IsNullOrEmpty(userId) || !_memories.TryGetValue(userId, out var memories) || memories.Count == 0)
            return string.Empty;

        var queryTokens = TextUtility.Tokenize(currentQuery);
        var now = DateTime.UtcNow;
        var scored = new List<(MemoryEntry Entry, double Weight)>();

        foreach (var entry in memories)
        {
            if (now - entry.Timestamp > _ttl) continue;

            var jaccard = queryTokens.Count > 0
                ? TextUtility.JaccardSimilarity(queryTokens, TextUtility.Tokenize(entry.Text))
                : 0.1;

            var ageDays = (now - entry.Timestamp).TotalDays;
            var recency = Math.Max(0.0, 1.0 - ageDays / 7.0);

            var weight = jaccard * 0.60 + recency * 0.40;
            if (weight >= 0.15)
                scored.Add((entry, weight));
        }

        scored = scored.OrderByDescending(x => x.Weight).ToList();

        var parts = new List<string>();
        var budgetUsed = 0;

        foreach (var (entry, weight) in scored)
        {
            if (budgetUsed >= maxBudget) break;

            var daysAgo = (int)((now - entry.Timestamp).TotalDays);
            var daysLabel = daysAgo == 0 ? "今天" : $"{daysAgo}天前";

            string snippet;
            if (weight > 0.5)
                snippet = ExtractSnippet(entry.Text, 120, 120);
            else if (weight > 0.25)
                snippet = ExtractSnippet(entry.Text, 60, 60);
            else
                snippet = ExtractSnippet(entry.Text, 30, 30);

            if (string.IsNullOrEmpty(snippet)) continue;

            var line = $"[{daysLabel}] [{entry.Type}] w={weight:F2} {snippet}";
            if (budgetUsed + line.Length > maxBudget && parts.Count > 0)
                break;

            parts.Add(line);
            budgetUsed += line.Length;
        }

        return string.Join("\n", parts);
    }

    public string InjectContext(string userId, string query)
    {
        return InjectWeightedContext(userId, query);
    }

    private static string? DetermineMemoryType(string text)
    {
        if (_HasDecisionKeywords(text)) return "decision";
        if (_HasPreferenceKeywords(text)) return "preference";
        if (_HasPendingKeywords(text)) return "pending";
        return null;
    }

    private static bool _HasDecisionKeywords(string text)
    {
        return DecisionKeywords.IsMatch(text);
    }

    private static bool _HasPreferenceKeywords(string text)
    {
        return PreferenceKeywords.IsMatch(text);
    }

    private static bool _HasPendingKeywords(string text)
    {
        return PendingKeywords.IsMatch(text);
    }

    private static string ExtractSnippet(string text, int minLen, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var cleaned = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (cleaned.Length <= maxLen)
            return cleaned.Length >= minLen ? cleaned : string.Empty;

        return cleaned[..(maxLen - 3)] + "...";
    }

    private void PruneMemories(string userId)
    {
        if (!_memories.TryGetValue(userId, out var memories))
            return;

        lock (_lock)
        {
            var now = DateTime.UtcNow;

            memories.RemoveAll(m => now - m.Timestamp > _ttl);

            if (memories.Count > MaxPerUser)
            {
                var toKeep = memories
                    .OrderByDescending(m => m.Timestamp)
                    .Take(MaxPerUser)
                    .ToList();

                memories.Clear();
                memories.AddRange(toKeep);
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_persistPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var data = _memories.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.ToList());

                var json = JsonSerializer.Serialize(data, JsonOptions);
                File.WriteAllText(_persistPath, json);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to save cross-session memories");
            }
        }
    }

    public void Load()
    {
        if (!File.Exists(_persistPath))
            return;

        try
        {
            var json = File.ReadAllText(_persistPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, List<MemoryEntry>>>(json, JsonOptions);
            if (data == null) return;

            foreach (var (userId, entries) in data)
                _memories[userId] = entries.ToList();

            _logger?.LogInformation("Loaded cross-session memories for {Count} users from {Path}",
                _memories.Count, _persistPath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load cross-session memories");
        }
    }
}
