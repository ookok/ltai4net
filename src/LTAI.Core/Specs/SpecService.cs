using System.Text.Json;

namespace LTAI.Core.Specs;

public enum SpecStatus { Draft, Clarified, Planned, Tasked, Implementing, Done }

public sealed record SpecManifest(
    string Name,
    string Description,
    SpecStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<string> Tags,
    string? TechStack = null);

/// <summary>
/// Manages spec/plan/tasks artifacts in <c>.livingtree/specs/</c>.
/// Each spec is a directory: <c>specs/{name}/spec.md</c>, <c>plan.md</c>, <c>tasks.md</c>.
/// A <c>manifest.json</c> tracks metadata.
/// </summary>
public sealed class SpecService
{
    private readonly string _baseDir;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public SpecService(string baseDir)
    {
        _baseDir = baseDir;
        Directory.CreateDirectory(_baseDir);
    }

    public IReadOnlyList<SpecManifest> List() => ListManifests();

    public SpecManifest? Get(string name)
    {
        var dir = SpecDir(name);
        if (!Directory.Exists(dir)) return null;
        return ReadManifest(name) ?? CreateManifest(name);
    }

    public string? ReadSpec(string name)
    {
        var path = Path.Combine(SpecDir(name), "spec.md");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void WriteSpec(string name, string content)
    {
        var dir = SpecDir(name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "spec.md"), content);
        TouchManifest(name);
    }

    public string? ReadPlan(string name)
    {
        var path = Path.Combine(SpecDir(name), "plan.md");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void WritePlan(string name, string content)
    {
        var dir = SpecDir(name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plan.md"), content);
        TouchManifest(name);
    }

    public string? ReadTasks(string name)
    {
        var path = Path.Combine(SpecDir(name), "tasks.md");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void WriteTasks(string name, string content)
    {
        var dir = SpecDir(name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "tasks.md"), content);
        TouchManifest(name);
    }

    public void SetStatus(string name, SpecStatus status)
    {
        var m = Get(name);
        if (m == null) return;
        WriteManifest(name, m with { Status = status, UpdatedAt = DateTime.UtcNow });
    }

    public bool Delete(string name)
    {
        var dir = SpecDir(name);
        if (!Directory.Exists(dir)) return false;
        Directory.Delete(dir, recursive: true);
        return true;
    }

    // ── Private ──

    private string SpecDir(string name) => Path.Combine(_baseDir, Sanitize(name));

    private static string Sanitize(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars()));

    private List<SpecManifest> ListManifests()
    {
        if (!Directory.Exists(_baseDir)) return [];
        var list = new List<SpecManifest>();
        foreach (var dir in Directory.GetDirectories(_baseDir))
        {
            var name = Path.GetFileName(dir);
            var m = ReadManifest(name);
            list.Add(m ?? CreateManifest(name));
        }
        return list.OrderByDescending(m => m.UpdatedAt).ToList();
    }

    private SpecManifest? ReadManifest(string name)
    {
        var path = Path.Combine(SpecDir(name), "manifest.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<SpecManifest>(File.ReadAllText(path), JsonOpts); }
        catch { return null; }
    }

    private SpecManifest CreateManifest(string name)
    {
        var m = new SpecManifest(name, "", SpecStatus.Draft, DateTime.UtcNow, DateTime.UtcNow, []);
        WriteManifest(name, m);
        return m;
    }

    private void WriteManifest(string name, SpecManifest m)
    {
        var dir = SpecDir(name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(m, JsonOpts));
    }

    private void TouchManifest(string name)
    {
        var m = Get(name);
        if (m != null) WriteManifest(name, m with { UpdatedAt = DateTime.UtcNow });
    }
}
