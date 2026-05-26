using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tools.Knowledge;

public enum ProjectStage { 招标, 受理, 拟批, 批复, 验收, Unknown }

public record FoodSource(string Domain, string Url, string Category, int ScanIntervalHours,
    int Priority, List<string> Fingerprints);

public record EntityNode(string Id, string Name, string Type, Dictionary<string, string> Properties);

public record EntityRelation(string FromId, string ToId, string Relation, double Confidence);

public record DailyBrief(DateTime Date, Dictionary<string, int> Stats, List<string> Highlights,
    List<string> Transitions, List<string> Recommendations);

public sealed class KnowledgeForager : IDisposable
{
    private readonly ConcurrentDictionary<string, FoodSource> _foodMap = new();
    private readonly ConcurrentDictionary<string, EntityNode> _entities = new();
    private readonly List<EntityRelation> _relations = new();
    private readonly List<Dictionary<string, object>> _projects = new();
    private static readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ILogger<KnowledgeForager> _logger;
    private readonly string _storeDir;
    private readonly object _lock = new();

    public KnowledgeForager(ILogger<KnowledgeForager>? logger = null)
    {
        _logger = logger ?? NullLogger<KnowledgeForager>.Instance;
        _storeDir = Path.Combine(Environment.GetEnvironmentVariable("LTAI_WORKSPACE") ?? Directory.GetCurrentDirectory(), ".livingtree", "forager");
        Directory.CreateDirectory(_storeDir);
        LoadFoodMap();
        LoadGraph();
    }

    public void Dispose() { }

    public void RegisterSite(string domain, string url, string category, int scanHours = 24, int priority = 5)
    {
        var source = new FoodSource(domain, url, category, scanHours, priority, new());
        _foodMap[domain] = source;
        SaveFoodMap();
    }

    public List<FoodSource> GetDueSources()
    {
        var now = DateTime.UtcNow;
        return _foodMap.Values
            .OrderByDescending(s => s.Priority)
            .ToList();
    }

    public async Task<Dictionary<string, List<string>>> PatrolAsync()
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var source in GetDueSources().Take(10))
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
                request.Headers.Add("User-Agent", "Mozilla/5.0 (compatible; LTAI-Forager/1.0)");
                var response = await _client.SendAsync(request).ConfigureAwait(false);
                var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var items = DigestResults(html, source.Category);
                result[source.Domain] = items;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Patrol failed: {Domain}", source.Domain);
            }
        }
        return result;
    }

    private List<string> DigestResults(string html, string category)
    {
        var items = new List<string>();
        var entities = ExtractEntities(html);
        foreach (var (name, type, props) in entities)
        {
            var id = HashMd5($"{name}:{type}");
            lock (_lock) { _entities[id] = new EntityNode(id, name, type, props); }
            items.Add($"{type}: {name}");
        }

        var projectNames = ExtractProjects(html);
        foreach (var name in projectNames)
        {
            lock (_lock)
            {
                if (!_projects.Any(p => p.TryGetValue("name", out var n) && n?.ToString() == name))
                    _projects.Add(new() { ["name"] = name, ["discovered"] = DateTime.UtcNow, ["stage"] = ProjectStage.招标.ToString() });
            }
            items.Add($"Project: {name}");
        }
        return items;
    }

    private static List<(string Name, string Type, Dictionary<string, string> Props)> ExtractEntities(string text)
    {
        var entities = new List<(string, string, Dictionary<string, string>)>();
        foreach (Match m in Regex.Matches(text, @"([\u4e00-\u9fff]{2,20}(?:公司|集团|厂|局|研究院|中心))"))
            entities.Add((m.Value, "Company", new() { ["type"] = "company" }));
        foreach (Match m in Regex.Matches(text, @"([\u4e00-\u9fff]{4,30}(?:项目|工程|园区|基地|示范区))"))
            entities.Add((m.Value, "Project", new() { ["type"] = "project" }));
        foreach (Match m in Regex.Matches(text, @"(GB\s*\d{4,}|HJ\s*\d{3,}|DB\d{2}/\d+)", RegexOptions.IgnoreCase))
            entities.Add((m.Value, "Standard", new() { ["type"] = "standard" }));
        return entities;
    }

    private static List<string> ExtractProjects(string text)
    {
        return Regex.Matches(text, @"([\u4e00-\u9fff]{4,30}(?:项目|工程))")
            .Select(m => m.Value).Distinct().ToList();
    }

    public async Task<List<Dictionary<string, object>>> HuntAsync(string query,
        Func<string, List<string>, int, CancellationToken, Task<List<Dictionary<string, string>>>>? searchFn = null)
    {
        if (searchFn == null) return new();
        var results = await searchFn(query, new() { "web" }, 10, CancellationToken.None);
        var items = new List<Dictionary<string, object>>();
        foreach (var r in results)
        {
            if (r.TryGetValue("Snippet", out var snippet) || r.TryGetValue("snippet", out snippet))
                items.Add(new() { ["source"] = r.GetValueOrDefault("Url", ""), ["content"] = snippet,
                    ["entities"] = ExtractEntities(snippet ?? "") });
        }
        return items;
    }

    public async Task<DailyBrief> GenerateDailyBriefAsync(Func<string, string, Task<string>>? chatFn = null)
    {
        var stats = new Dictionary<string, int>
        {
            ["registered_sites"] = _foodMap.Count,
            ["entities"] = _entities.Count,
            ["projects"] = _projects.Count,
            ["relations"] = _relations.Count
        };

        var highlights = new List<string>();
        lock (_lock)
        {
            var recentProjects = _projects.Where(p =>
                p.TryGetValue("discovered", out var d) && d is DateTime dt && (DateTime.UtcNow - dt).TotalDays < 30)
                .ToList();
            highlights.Add($"{recentProjects.Count} active projects (30-day window)");
        }

        var companyCounts = new Dictionary<string, int>();
        lock (_lock)
        {
            foreach (var e in _entities.Values.Where(e => e.Type == "Company"))
            {
                companyCounts[e.Name] = companyCounts.GetValueOrDefault(e.Name) + 1;
            }
        }
        foreach (var kv in companyCounts.OrderByDescending(kv => kv.Value).Take(5))
            highlights.Add($"{kv.Key}: {kv.Value} mentions");

        var recommendations = new List<string>();
        if (_foodMap.Count < 5) recommendations.Add("Consider adding more monitoring sources");
        if (_entities.Count > 1000) recommendations.Add("Entity count high - consider periodic cleanup");

        return new DailyBrief(DateTime.UtcNow, stats, highlights,
            _projects.Select(p => p.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "").Where(s => !string.IsNullOrEmpty(s)).ToList(),
            recommendations);
    }

    private void LoadFoodMap()
    {
        var path = Path.Combine(_storeDir, "food_map.json");
        if (!File.Exists(path)) return;
        try
        {
            var sources = JsonSerializer.Deserialize<List<FoodSource>>(File.ReadAllText(path));
            if (sources != null) foreach (var s in sources) _foodMap[s.Domain] = s;
        }
        catch { /* non-fatal */ }
    }

    private void SaveFoodMap()
    {
        File.WriteAllText(Path.Combine(_storeDir, "food_map.json"),
            JsonSerializer.Serialize(_foodMap.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }));
    }

    private void LoadGraph()
    {
        var path = Path.Combine(_storeDir, "graph.json");
        if (!File.Exists(path)) return;
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
            if (json.TryGetProperty("nodes", out var nodes))
            {
                foreach (var n in nodes.EnumerateArray())
                {
                    var node = JsonSerializer.Deserialize<EntityNode>(n.GetRawText());
                    if (node != null) _entities[node.Id] = node;
                }
            }
            if (json.TryGetProperty("relations", out var rels))
                foreach (var r in rels.EnumerateArray())
                    _relations.Add(JsonSerializer.Deserialize<EntityRelation>(r.GetRawText())!);
            if (json.TryGetProperty("projects", out var projs))
                foreach (var p in projs.EnumerateArray())
                    _projects.Add(JsonSerializer.Deserialize<Dictionary<string, object>>(p.GetRawText())!);
        }
        catch { /* non-fatal */ }
    }

    public void SaveGraph()
    {
        lock (_lock)
        {
            File.WriteAllText(Path.Combine(_storeDir, "graph.json"),
                JsonSerializer.Serialize(new { nodes = _entities.Values.ToList(), relations = _relations,
                    projects = _projects }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static string HashMd5(string input)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input)))[..12];
}
