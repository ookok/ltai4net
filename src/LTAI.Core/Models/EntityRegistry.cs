using System.Text.Json;

namespace LTAI.Core.Models;

public sealed class EntityEntry
{
    public string Id { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string Description { get; set; } = "";
    public Dictionary<string, object> Metadata { get; set; } = new();
    public List<string> Aliases { get; set; } = new();
    public Dictionary<string, string> LinkedEntities { get; set; } = new();
    public double CreatedAt { get; set; }
    public double UpdatedAt { get; set; }
}

public sealed class EntityRegistry
{
    private static readonly Lazy<EntityRegistry> _instance = new(() => new EntityRegistry());
    public static EntityRegistry Instance => _instance.Value;

    private readonly Dictionary<string, EntityEntry> _entries = new();
    private readonly string _persistPath;
    private readonly object _lock = new();

    private EntityRegistry(string persistPath = ".livingtree/entities.json")
    {
        _persistPath = persistPath;
        Load();
        if (_entries.Count == 0)
        {
            SeedDefaults();
            Save();
        }
    }

    public EntityEntry Register(string ns, string key, string name, string entityType = "entity",
        string description = "", List<string>? aliases = null, Dictionary<string, object>? metadata = null)
    {
        var entityId = $"{ns}:{key}";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        aliases ??= new List<string>();
        metadata ??= new Dictionary<string, object>();

        EntityEntry entry;
        lock (_lock)
        {
            if (_entries.TryGetValue(entityId, out var existing))
            {
                existing.Name = name;
                existing.Description = description;
                existing.Metadata = metadata;
                existing.Aliases = existing.Aliases.Union(aliases).ToList();
                existing.UpdatedAt = now;
                Save();
                return existing;
            }

            entry = new EntityEntry
            {
                Id = entityId,
                Namespace = ns,
                Key = key,
                Name = name,
                EntityType = entityType,
                Description = description,
                Metadata = metadata,
                Aliases = aliases,
                CreatedAt = now,
                UpdatedAt = now
            };
            _entries[entityId] = entry;
        }

        Save();
        return entry;
    }

    public EntityEntry? Resolve(string idOrAlias)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(idOrAlias, out var entry))
                return entry;

            var lower = idOrAlias.ToLower();
            return _entries.Values.FirstOrDefault(e =>
                e.Aliases.Any(a => a.ToLower() == lower));
        }
    }

    public List<EntityEntry> GetByNamespace(string ns)
    {
        lock (_lock)
        {
            return _entries.Values.Where(e => e.Namespace == ns).ToList();
        }
    }

    public bool Link(string sourceId, string targetId, string relation = "related_to")
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(sourceId, out var src) || !_entries.TryGetValue(targetId, out var tgt))
                return false;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            src.LinkedEntities[targetId] = relation;
            tgt.LinkedEntities[sourceId] = relation;
            src.UpdatedAt = now;
            tgt.UpdatedAt = now;
        }
        Save();
        return true;
    }

    public Dictionary<string, string> GetReferences(string entityId)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(entityId, out var entry)
                ? new Dictionary<string, string>(entry.LinkedEntities)
                : new Dictionary<string, string>();
        }
    }

    public List<EntityEntry> Search(string query)
    {
        var q = query.ToLower();
        lock (_lock)
        {
            return _entries.Values
                .Where(e => e.Name.ToLower().Contains(q) || e.Aliases.Any(a => a.ToLower().Contains(q)))
                .ToList();
        }
    }

    public Dictionary<string, object> GetStats()
    {
        var byNs = new Dictionary<string, int>();
        var byType = new Dictionary<string, int>();
        lock (_lock)
        {
            foreach (var e in _entries.Values)
            {
                byNs[e.Namespace] = byNs.GetValueOrDefault(e.Namespace) + 1;
                byType[e.EntityType] = byType.GetValueOrDefault(e.EntityType) + 1;
            }
        }

        return new Dictionary<string, object>
        {
            ["total"] = _entries.Count,
            ["by_namespace"] = byNs,
            ["by_type"] = byType
        };
    }

    private void Save()
    {
        var dir = global::System.IO.Path.GetDirectoryName(_persistPath);
        if (dir != null) global::System.IO.Directory.CreateDirectory(dir);

        var data = _entries.Values.Select(e => new Dictionary<string, object>
        {
            ["id"] = e.Id,
            ["namespace"] = e.Namespace,
            ["key"] = e.Key,
            ["name"] = e.Name,
            ["type"] = e.EntityType,
            ["description"] = e.Description,
            ["metadata"] = e.Metadata,
            ["aliases"] = e.Aliases,
            ["linked_entities"] = e.LinkedEntities,
            ["created_at"] = e.CreatedAt,
            ["updated_at"] = e.UpdatedAt
        }).ToList();

        global::System.IO.File.WriteAllText(_persistPath, JsonSerializer.Serialize(data));
    }

    private void Load()
    {
        if (!global::System.IO.File.Exists(_persistPath)) return;

        try
        {
            var json = global::System.IO.File.ReadAllText(_persistPath);
            var data = JsonSerializer.Deserialize<List<JsonElement>>(json);
            if (data == null) return;

            foreach (var item in data)
            {
                var entry = new EntityEntry
                {
                    Id = item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Namespace = item.TryGetProperty("namespace", out var ns) ? ns.GetString() ?? "" : "",
                    Key = item.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "",
                    Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    EntityType = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                    Description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    CreatedAt = item.TryGetProperty("created_at", out var ca) ? ca.GetDouble() : 0,
                    UpdatedAt = item.TryGetProperty("updated_at", out var ua) ? ua.GetDouble() : 0
                };
                if (item.TryGetProperty("aliases", out var aliases))
                    entry.Aliases = aliases.EnumerateArray().Select(a => a.GetString() ?? "").ToList();
                if (item.TryGetProperty("linked_entities", out var links))
                    entry.LinkedEntities = links.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");

                lock (_lock) { _entries[entry.Id] = entry; }
            }
        }
        catch { /* non-fatal */ }
    }

    private void SeedDefaults()
    {
        var defaults = new (string ns, string key, string name, string type, string desc, string[] aliases)[]
        {
            ("kg", "python-lang", "Python", "entity", "Python programming language", new[] { "python", "py" }),
            ("kg", "llm-concept", "LLM", "entity", "Large Language Model", new[] { "large language model" }),
            ("glossary", "unit-test", "Unit Test", "term", "Unit testing methodology", new[] { "unit test" }),
            ("skill", "code-generation", "Code Generation", "skill", "Generate code from requirements", new[] { "code gen" }),
            ("skill", "code-review", "Code Review", "skill", "Review code for quality", new[] { "code review" }),
            ("skill", "reasoning", "Reasoning", "skill", "Logical reasoning and analysis", new[] { "logic" }),
            ("skill", "knowledge-search", "Knowledge Search", "skill", "Search knowledge base", new[] { "search" }),
            ("code", "livingtree-main", "LivingTreeAlAgent", "code", "Main repository", new[] { "lta", "livingtree" }),
            ("code", "treellm-router", "TreeLLM Router", "code", "Multi-provider routing", new[] { "treellm" }),
        };

        foreach (var (ns, key, name, type, desc, aliases) in defaults)
            Register(ns, key, name, type, desc, aliases.ToList());

        Link("kg:python-lang", "skill:code-generation", "related_to");
        Link("kg:llm-concept", "skill:reasoning", "related_to");
    }
}
