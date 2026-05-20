using System.Text.Json;

namespace LTAI.Capability.Tools;

public sealed class PublicApisEntry
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Auth { get; set; }
    public bool Https { get; set; }
    public bool Cors { get; set; }
}

public sealed class PublicApisResource
{
    private static readonly Lazy<PublicApisResource> _instance = new(() => new PublicApisResource());
    public static PublicApisResource Instance => _instance.Value;

    private const string RepoUrl = "https://raw.githubusercontent.com/public-apis/public-apis/master/README.md";
    private readonly HttpClient _http;
    private readonly Dictionary<string, List<PublicApisEntry>> _categories = new();
    private readonly object _lock = new();
    private bool _loaded;

    private PublicApisResource()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task LoadAsync()
    {
        if (_loaded) return;

        try
        {
            var md = await _http.GetStringAsync(RepoUrl);
            ParseReadme(md);
            _loaded = true;
        }
        catch
        {
            SeedDefaults();
            _loaded = true;
        }
    }

    public List<string> ListCategories()
    {
        EnsureLoaded();
        lock (_lock) { return _categories.Keys.OrderBy(k => k).ToList(); }
    }

    public List<PublicApisEntry> ListCategory(string category)
    {
        EnsureLoaded();
        lock (_lock) { return _categories.GetValueOrDefault(category)?.ToList() ?? new(); }
    }

    public List<PublicApisEntry> Search(string query)
    {
        EnsureLoaded();
        var q = query.ToLower();
        var results = new List<PublicApisEntry>();
        lock (_lock)
        {
            foreach (var (_, entries) in _categories)
                results.AddRange(entries.Where(e =>
                    e.Name.ToLower().Contains(q) || e.Description.ToLower().Contains(q)));
        }
        return results.Take(20).ToList();
    }

    public Dictionary<string, object> GetStats()
    {
        EnsureLoaded();
        lock (_lock)
        {
            var total = _categories.Values.Sum(c => c.Count);
            return new()
            {
                ["categories"] = _categories.Count,
                ["apis"] = total,
                ["loaded"] = _loaded
            };
        }
    }

    private void EnsureLoaded()
    {
        if (!_loaded)
        {
            SeedDefaults();
            _loaded = true;
        }
    }

    private void ParseReadme(string markdown)
    {
        var lines = markdown.Split('\n');
        var currentCategory = "";
        var inTable = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("### "))
            {
                currentCategory = line[4..].Trim();
                inTable = false;
                continue;
            }

            if (line.Trim().StartsWith("|---") || line.Trim().StartsWith("| ---"))
            {
                inTable = true;
                continue;
            }

            if (!inTable || string.IsNullOrWhiteSpace(line) || !line.StartsWith("|"))
                continue;

            var cells = line.Split('|')
                .Select(c => c.Trim())
                .Where(c => c.Length > 0)
                .ToList();

            if (cells.Count >= 4)
            {
                var entry = new PublicApisEntry
                {
                    Name = cells[0],
                    Description = cells.Count > 1 ? cells[1] : "",
                    Auth = cells.Count > 2 && cells[2].ToLower() != "no",
                    Https = cells.Count > 3 && cells[3].ToLower() == "yes",
                    Cors = cells.Count > 4 && cells[4].ToLower() == "yes",
                    Category = currentCategory
                };

                if (!string.IsNullOrEmpty(currentCategory))
                {
                    if (!_categories.ContainsKey(currentCategory))
                        _categories[currentCategory] = new List<PublicApisEntry>();
                    _categories[currentCategory].Add(entry);
                }
            }
        }
    }

    private void SeedDefaults()
    {
        var defaults = new Dictionary<string, List<(string name, string desc, bool auth, bool https)>>
        {
            ["Weather"] = new() { ("Open-Meteo", "Free weather API", false, true), ("OpenWeatherMap", "Weather forecasts and current data", true, true) },
            ["Development"] = new() { ("GitHub", "Developer platform", true, true), ("GitLab", "DevOps platform", true, true), ("StackExchange", "Q&A platform", true, true) },
            ["Finance"] = new() { ("ExchangeRate-API", "Currency exchange rates", true, true), ("CoinGecko", "Cryptocurrency data", false, true) },
            ["Science & Math"] = new() { ("NASA", "NASA data APIs", false, true), ("arXiv", "Scientific papers", false, true) },
            ["Geocoding"] = new() { ("Nominatim", "OpenStreetMap geocoding", false, true), ("Mapbox", "Maps and geocoding", true, true) },
            ["News"] = new() { ("HackerNews", "Tech news", false, true), ("NewsAPI", "News headlines", true, true) }
        };

        foreach (var (cat, apis) in defaults)
        {
            _categories[cat] = apis.Select(a => new PublicApisEntry
            {
                Name = a.name, Description = a.desc, Auth = a.auth, Https = a.https, Category = cat
            }).ToList();
        }
    }
}
