using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Knowledge.Core;

public sealed class PromptService : IAsyncDisposable
{
    private readonly PromptLoader _loader;
    private readonly ILogger<PromptService> _logger;
    private readonly ConcurrentDictionary<string, PromptFile> _prompts = new();
    private readonly ConcurrentDictionary<string, PromptTemplate> _templates = new();
    private readonly ConcurrentDictionary<string, List<string>> _byDomain = new();
    private readonly ConcurrentDictionary<string, List<string>> _byTag = new();
    private readonly ConcurrentDictionary<string, int> _selectionCounts = new();
    private readonly string _promptRoot;
    private PromptAbTestManager? _abManager;

    public IReadOnlyDictionary<string, PromptFile> All => _prompts;
    public int PromptCount => _prompts.Count;
    public int TemplateCount => _templates.Count;

    private static readonly Regex Placeholder = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    public PromptService(
        PromptLoader loader,
        ILogger<PromptService> logger,
        string? promptRoot = null)
    {
        _loader = loader;
        _logger = logger;
        _promptRoot = promptRoot ?? OptionService.Get("paths.prompts") ?? Path.Combine(AppContext.BaseDirectory, "prompts");
    }

    public async Task LoadAllAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_promptRoot))
        {
            Directory.CreateDirectory(_promptRoot);
            _logger.LogInformation("Prompt root created: {Path}", _promptRoot);
            return;
        }

        var mdFiles = Directory.GetFiles(_promptRoot, "*.md", SearchOption.AllDirectories);
        _logger.LogInformation("Loading {Count} prompt files from {Root}", mdFiles.Length, _promptRoot);

        foreach (var file in mdFiles)
        {
            if (file.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)) continue;

            var prompt = await _loader.LoadPromptAsync(file, ct).ConfigureAwait(false);
            if (prompt != null)
            {
                Register(prompt);
                _logger.LogDebug("Loaded prompt: {Name} [{Domain}]", prompt.Name, prompt.Domain);
            }

            var template = await _loader.LoadTemplateAsync(file, ct).ConfigureAwait(false);
            if (template != null && template.Sections.Count > 0)
            {
                RegisterTemplate(template);
                _logger.LogDebug("Loaded template: {Name} [{Domain}]", template.Name, template.Domain);
            }
        }
    }

    public void Register(PromptFile prompt)
    {
        _prompts[prompt.Id] = prompt;

        var domain = prompt.Domain;
        if (!_byDomain.ContainsKey(domain))
            _byDomain[domain] = new List<string>();
        _byDomain[domain].Add(prompt.Id);

        foreach (var tag in prompt.Tags)
        {
            if (!_byTag.ContainsKey(tag))
                _byTag[tag] = new List<string>();
            _byTag[tag].Add(prompt.Id);
        }
    }

    public void RegisterTemplate(PromptTemplate template)
    {
        _templates[template.Name] = template;
    }

    public PromptFile? GetById(string id)
    {
        _prompts.TryGetValue(id, out var pf);
        return pf;
    }

    public PromptFile? GetByName(string name)
    {
        return _prompts.Values.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public List<PromptFile> GetByDomain(string domain)
    {
        if (!_byDomain.TryGetValue(domain, out var ids))
            return new List<PromptFile>();

        return ids.Select(id => _prompts.TryGetValue(id, out var pf) ? pf : null)
            .Where(p => p != null)
            .Cast<PromptFile>()
            .ToList();
    }

    public List<PromptFile> SelectBest(string task, string domain = "general",
        int maxResults = 3)
    {
        var candidates = new List<(PromptFile Prompt, double Score)>();

        var domainPrompts = GetByDomain(domain);
        var generalPrompts = domain != "general" ? GetByDomain("general") : new List<PromptFile>();
        var allCandidates = domainPrompts.Concat(generalPrompts).DistinctBy(p => p.Id);

        foreach (var prompt in allCandidates)
        {
            double score = 0;

            foreach (var trigger in prompt.Triggers)
            {
                if (task.Contains(trigger.Pattern, StringComparison.OrdinalIgnoreCase))
                    score += trigger.Weight * 2;
            }

            foreach (var tag in prompt.Tags)
            {
                if (task.Contains(tag, StringComparison.OrdinalIgnoreCase))
                    score += 0.5;
            }

            if (prompt.Evolution.IsReliable)
                score += 0.3;

            var txt = prompt.Description + " " + prompt.Template;
            var overlap = task.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Count(w => txt.Contains(w, StringComparison.OrdinalIgnoreCase));
            score += overlap * 0.1;

            if (score > 0)
                candidates.Add((prompt, score));
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .Take(maxResults)
            .Select(c =>
            {
                _selectionCounts.AddOrUpdate(c.Prompt.Id, 1, (_, v) => v + 1);
                return c.Prompt;
            })
            .ToList();
    }

    public PromptRenderResult Render(string promptId, Dictionary<string, string>? variables = null)
    {
        var prompt = GetById(promptId) ?? GetByName(promptId);
        if (prompt == null)
            return new PromptRenderResult { PromptId = promptId, Error = "Prompt not found" };

        return RenderInternal(prompt, variables ?? new());
    }

    public PromptRenderResult RenderByName(string name, Dictionary<string, string>? variables = null)
    {
        var prompt = GetByName(name);
        if (prompt == null)
            return new PromptRenderResult { PromptId = name, Error = "Prompt not found" };

        return RenderInternal(prompt, variables ?? new());
    }

    private PromptRenderResult RenderInternal(PromptFile prompt, Dictionary<string, string> variables)
    {
        var missing = new List<string>();
        var template = prompt.Template;

        var matches = Placeholder.Matches(template);
        foreach (Match match in matches)
        {
            var varName = match.Groups[1].Value;
            var placeholder = match.Value;

            if (variables.TryGetValue(varName, out var value))
            {
                template = template.Replace(placeholder, value);
            }
            else
            {
                var varDef = prompt.Variables.FirstOrDefault(v =>
                    v.Name.Equals(varName, StringComparison.OrdinalIgnoreCase));
                if (varDef?.Default != null)
                {
                    template = template.Replace(placeholder, varDef.Default);
                }
                else if (varDef?.Required == true)
                {
                    missing.Add(varName);
                }
                else
                {
                    template = template.Replace(placeholder, $"[{varName}]");
                }
            }
        }

        template = Placeholder.Replace(template, m =>
        {
            var varName = m.Groups[1].Value;
            return missing.Contains(varName) ? m.Value : $"[{varName}]";
        });

        return new PromptRenderResult
        {
            PromptId = prompt.Id,
            Rendered = template.Trim(),
            Success = missing.Count == 0,
            MissingVariables = missing
        };
    }

    public async Task<PromptRenderResult> ComposeAsync(
        string templateName,
        Dictionary<string, string>? variables = null,
        CancellationToken ct = default)
    {
        if (!_templates.TryGetValue(templateName, out var template))
            return new PromptRenderResult { PromptId = templateName, Error = "Template not found" };

        var parts = new List<string>();

        foreach (var section in template.Sections.OrderBy(s => s.Order))
        {
            if (!string.IsNullOrEmpty(section.PromptId))
            {
                var prompt = GetById(section.PromptId) ?? GetByName(section.PromptId);
                if (prompt != null)
                {
                    var rendered = RenderInternal(prompt, variables ?? new());
                    parts.Add(rendered.Rendered);
                }
                else if (!section.Optional)
                {
                    _logger.LogWarning("Compose: referenced prompt '{Id}' not found", section.PromptId);
                }
            }
            else if (!string.IsNullOrEmpty(section.Name))
            {
                parts.Add(section.Name);
            }
        }

        var composed = string.Join("\n\n", parts);

        if (composed.Length > template.MaxTotalChars)
        {
            _logger.LogWarning("Compose: output length {Len} exceeds max {Max}, truncating",
                composed.Length, template.MaxTotalChars);
            composed = composed[..template.MaxTotalChars];
        }

        return new PromptRenderResult
        {
            PromptId = templateName,
            Rendered = composed,
            Success = true
        };
    }

    public void SetAbTestManager(PromptAbTestManager manager) => _abManager = manager;

    public AbTestResult? GetBestWithAbTest(string task, string domain = "general",
        Dictionary<string, string>? variables = null)
    {
        if (_abManager == null) return null;

        var groups = _abManager.GetGroupsByDomain(domain);
        foreach (var group in groups)
        {
            return _abManager.SelectBestVariant(group.GroupId, variables);
        }
        return null;
    }

    public void RecordFeedback(string promptId, bool success)
    {
        var prompt = GetById(promptId) ?? GetByName(promptId);
        if (prompt == null) return;

        if (success)
            prompt.Evolution.RecordSuccess();
        else
            prompt.Evolution.RecordFailure();

        PersistEvolution(prompt);

        _logger.LogDebug("Prompt feedback: {Name} success={Success} rate={Rate}",
            prompt.Name, success, prompt.Evolution.SuccessRate);
    }

    private void PersistEvolution(PromptFile prompt)
    {
        if (string.IsNullOrEmpty(prompt.SourceFile)) return;

        try
        {
            var metaPath = prompt.SourceFile + ".meta.json";
            var json = JsonSerializer.Serialize(prompt.Evolution, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metaPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist evolution for prompt '{Name}'", prompt.Name);
        }
    }

    public async Task<PromptFile> CreateAndSaveAsync(
        string name,
        string domain,
        string template,
        List<PromptVariable>? variables = null,
        List<PromptTrigger>? triggers = null,
        List<string>? tags = null,
        CancellationToken ct = default)
    {
        var pf = new PromptFile
        {
            Name = name,
            Domain = domain,
            Template = template,
            Variables = variables ?? new(),
            Triggers = triggers ?? new(),
            Tags = tags ?? new()
        };

        await _loader.SaveAsync(pf, _promptRoot, ct).ConfigureAwait(false);
        Register(pf);
        return pf;
    }

    public PromptFile? GetBestForTask(string task, string domain = "general")
    {
        var prompts = SelectBest(task, domain, maxResults: 1);
        return prompts.FirstOrDefault();
    }

    public async ValueTask DisposeAsync()
    {
        _prompts.Clear();
        _templates.Clear();
        _byDomain.Clear();
        _byTag.Clear();
        _selectionCounts.Clear();
        await Task.CompletedTask;
    }
}
