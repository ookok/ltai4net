using System.Text.Json;
using System.Text.Json.Nodes;

namespace LTAI.Desktop.Debugging;

public sealed record Breakpoint(string File, int Line, bool IsEnabled = true, string? Condition = null);

public sealed class BreakpointManager
{
    private readonly string _filePath;
    private readonly Dictionary<string, HashSet<int>> _bps = new(StringComparer.OrdinalIgnoreCase);

    public event Action? BreakpointsChanged;
    public IReadOnlyCollection<Breakpoint> All => _bps
        .SelectMany(kv => kv.Value.Select(l => new Breakpoint(kv.Key, l)))
        .ToList();

    public BreakpointManager(string workspaceRoot)
    {
        _filePath = Path.Combine(workspaceRoot, ".livingtree", "launch.vs.json");
        Load();
    }

    public bool HasBreakpoint(string file, int line)
        => _bps.TryGetValue(file, out var lines) && lines.Contains(line);

    public void Toggle(string file, int line)
    {
        if (!_bps.TryGetValue(file, out var lines))
        {
            _bps[file] = [line];
        }
        else
        {
            if (!lines.Remove(line))
                lines.Add(line);
            if (lines.Count == 0)
                _bps.Remove(file);
        }
        Save();
        BreakpointsChanged?.Invoke();
    }

    public void Set(string file, int[] lines)
    {
        _bps[file] = new HashSet<int>(lines);
        if (_bps[file].Count == 0) _bps.Remove(file);
        Save();
        BreakpointsChanged?.Invoke();
    }

    public int[] GetLines(string file)
        => _bps.TryGetValue(file, out var lines) ? lines.OrderBy(l => l).ToArray() : [];

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = JsonNode.Parse(File.ReadAllText(_filePath)) as JsonObject;
            if (json == null) return;
            var bps = json["breakpoints"] as JsonArray;
            if (bps == null) return;

            foreach (var bp in bps)
            {
                var f = bp!["file"]?.GetValue<string>();
                var l = bp["line"]?.GetValue<int>();
                if (f != null && l.HasValue)
                {
                    if (!_bps.TryGetValue(f, out var lines))
                        _bps[f] = lines = [];
                    lines.Add(l.Value);
                }
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (dir != null) Directory.CreateDirectory(dir);

            var bps = new JsonArray();
            foreach (var (file, lines) in _bps)
            {
                foreach (var line in lines)
                {
                    bps.Add(new JsonObject
                    {
                        ["file"] = file,
                        ["line"] = line,
                        ["enabled"] = true,
                    });
                }
            }

            var json = new JsonObject
            {
                ["version"] = 1,
                ["breakpoints"] = bps,
            };
            File.WriteAllText(_filePath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
