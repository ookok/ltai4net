using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

public sealed record IntentRule
{
    public string Domain { get; init; } = "";
    public float Quality { get; init; }
    public float Speed { get; init; }
    public float Cost { get; init; }
    public string Description { get; init; } = "";
    public string[] Keywords { get; init; } = Array.Empty<string>();
    public Regex[] Patterns { get; init; } = Array.Empty<Regex>();
    public string SourceFile { get; init; } = "";
}

public sealed class RuleLoader
{
    private readonly string _rulesDir;
    private readonly ILogger<RuleLoader> _logger;

    private static readonly Regex HeaderLine = new(@"^#\s+rule:\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex KeyValue = new(@"^(\w[\w_]*):\s*(.+)$", RegexOptions.Compiled);

    public RuleLoader(string? rulesDir = null, ILogger<RuleLoader>? logger = null)
    {
        _rulesDir = rulesDir ?? Path.Combine(AppContext.BaseDirectory, "rules");
        _logger = logger ?? NullLogger<RuleLoader>.Instance;
    }

    public async Task<IReadOnlyList<IntentRule>> LoadAllAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_rulesDir))
        {
            _logger.LogWarning("Rules directory not found: {Dir}", _rulesDir);
            return Array.Empty<IntentRule>();
        }

        var files = Directory.GetFiles(_rulesDir, "*.md");
        var rules = new List<IntentRule>(files.Length);

        foreach (var file in files)
        {
            var rule = await LoadAsync(file, ct).ConfigureAwait(false);
            if (rule != null)
                rules.Add(rule);
        }

        _logger.LogInformation("Loaded {Count} intent rules from {Dir}", rules.Count, _rulesDir);
        return rules;
    }

    public async Task<IntentRule?> LoadAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var text = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            return Parse(filePath, text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load rule from {Path}", filePath);
            return null;
        }
    }

    public IntentRule? Parse(string filePath, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string domain = "";
        float quality = 0.75f, speed = 0.35f, cost = 0.35f;
        string description = "";
        var keywords = new List<string>();
        var patterns = new List<Regex>();
        var section = "header";

        foreach (var line in lines)
        {
            if (line.StartsWith("## "))
            {
                section = line[3..].Trim().ToLowerInvariant();
                continue;
            }

            if (section == "header")
            {
                var hm = HeaderLine.Match(line);
                if (hm.Success)
                {
                    domain = hm.Groups[1].Value.Trim();
                    continue;
                }
                var kv = KeyValue.Match(line);
                if (kv.Success)
                {
                    var k = kv.Groups[1].Value;
                    var v = kv.Groups[2].Value.Trim();
                    switch (k)
                    {
                        case "quality": float.TryParse(v, out quality); break;
                        case "speed": float.TryParse(v, out speed); break;
                        case "cost": float.TryParse(v, out cost); break;
                        case "description": description = v; break;
                    }
                }
            }
            else if (section == "keywords")
            {
                var kw = line.Trim();
                if (string.IsNullOrEmpty(kw)) continue;
                kw = kw.TrimStart('-', ' ').Trim();
                if (!string.IsNullOrEmpty(kw))
                    keywords.Add(kw);
            }
            else if (section == "regex")
            {
                var re = line.Trim();
                if (string.IsNullOrEmpty(re)) continue;
                re = re.TrimStart('-', ' ').Trim();
                if (!string.IsNullOrEmpty(re))
                {
                    try { patterns.Add(new Regex(re, RegexOptions.Compiled | RegexOptions.IgnoreCase)); }
                    catch { _logger.LogDebug("Invalid regex in {File}: {Pattern}", filePath, re); }
                }
            }
        }

        if (string.IsNullOrEmpty(domain))
            domain = Path.GetFileNameWithoutExtension(filePath);

        return new IntentRule
        {
            Domain = domain,
            Quality = quality,
            Speed = speed,
            Cost = cost,
            Description = description,
            Keywords = keywords.ToArray(),
            Patterns = patterns.ToArray(),
            SourceFile = filePath
        };
    }
}
