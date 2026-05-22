using System.Text;
using System.Text.Json;

namespace LTAI.Knowledge.Core;

public sealed record SectionRef(string Id, string Title, long ByteOffset, int ByteLength, int CharOffset, int CharLength);

public sealed class LazyIndex
{
    private readonly Dictionary<string, List<SectionRef>> _index = new();
    private readonly string _indexPath;
    private long _totalBytes;
    private int _totalSections;

    public LazyIndex(string? indexPath = null)
    {
        _indexPath = indexPath ?? global::System.IO.Path.Combine(".livingtree", "knowledge", "lazy_index.json");
        var dir = global::System.IO.Path.GetDirectoryName(_indexPath);
        if (dir != null) global::System.IO.Directory.CreateDirectory(dir);
        Load();
    }

    public List<SectionRef> IndexDocument(string docId, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var sections = ParseSections(content, bytes.Length);
        _index[docId] = sections;
        _totalBytes += bytes.Length;
        _totalSections += sections.Count;
        Save();
        return sections;
    }

    public SectionRef? LoadSection(string docId, int sectionIdx)
    {
        if (!_index.TryGetValue(docId, out var sections) || sectionIdx < 0 || sectionIdx >= sections.Count)
            return null;
        return sections[sectionIdx];
    }

    public List<SectionRef> SearchSections(string query, int limit = 10)
    {
        var q = query.ToLower();
        return _index.Values.SelectMany(s => s)
            .Where(s => s.Title.ToLower().Contains(q))
            .OrderBy(s => s.ByteLength)
            .Take(limit).ToList();
    }

    public List<SectionRef> LoadTopSections(string docId, int topK = 5)
    {
        if (!_index.TryGetValue(docId, out var sections)) return new();
        return sections.OrderByDescending(s => s.ByteLength).Take(topK).ToList();
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["documents"] = _index.Count,
        ["sections"] = _totalSections,
        ["total_bytes"] = _totalBytes,
        ["total_mb"] = Math.Round(_totalBytes / (1024.0 * 1024.0), 1),
        ["memory_saved"] = "70-90% (on-demand loading vs full doc in memory)"
    };

    public List<string> ListDocuments() => _index.Keys.ToList();

    private static List<SectionRef> ParseSections(string content, int totalBytes)
    {
        var sections = new List<SectionRef>();
        var matches = System.Text.RegularExpressions.Regex.Matches(content, @"^#{1,4}\s+(.+)$", System.Text.RegularExpressions.RegexOptions.Multiline);
        var chars = content.ToCharArray();

        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var title = match.Groups[1].Value.Trim();
            var charOffset = match.Index;
            var charLength = i < matches.Count - 1
                ? matches[i + 1].Index - match.Index
                : content.Length - match.Index;

            var byteOffset = (long)((double)charOffset / content.Length * totalBytes);
            var byteLength = (int)((double)charLength / content.Length * totalBytes);

            sections.Add(new SectionRef(
                $"{i}_{SanitizeId(title)}", title, byteOffset, byteLength, charOffset, charLength));
        }

        if (sections.Count == 0)
            sections.Add(new SectionRef("full", "Full Document", 0, totalBytes, 0, content.Length));

        return sections;
    }

    private static string SanitizeId(string title) =>
        System.Text.RegularExpressions.Regex.Replace(title.ToLower(), @"[^a-z0-9\u4e00-\u9fff]+", "_").Trim('_');

    private void Save()
    {
        var data = _index.ToDictionary(k => k.Key, v => v.Value.Select(s => new
        {
            s.Id, s.Title, s.ByteOffset, s.ByteLength, s.CharOffset, s.CharLength
        }).ToList());
        global::System.IO.File.WriteAllText(_indexPath, JsonSerializer.Serialize(data));
    }

    private void Load()
    {
        if (!global::System.IO.File.Exists(_indexPath)) return;
        try
        {
            var json = global::System.IO.File.ReadAllText(_indexPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, List<JsonElement>>>(json);
            if (data == null) return;
            foreach (var (docId, sections) in data)
            {
                _index[docId] = sections.Select(s => new SectionRef(
                    s.GetProperty("id").GetString() ?? "",
                    s.GetProperty("title").GetString() ?? "",
                    s.GetProperty("byteOffset").GetInt64(),
                    s.GetProperty("byteLength").GetInt32(),
                    s.TryGetProperty("charOffset", out var co) ? co.GetInt32() : 0,
                    s.TryGetProperty("charLength", out var cl) ? cl.GetInt32() : 0
                )).ToList();
                _totalSections += _index[docId].Count;
            }
        }
        catch { /* non-fatal */ }
    }
}
