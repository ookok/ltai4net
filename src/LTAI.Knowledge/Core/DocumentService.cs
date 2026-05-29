using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Knowledge.Core;

/// <summary>
/// Unified file-based document, prompt, and memory access.
/// Replaces DocumentStore + PromptService + MemoryFilesService.
/// </summary>
public sealed class DocumentService
{
    private readonly string _baseDir;

    public DocumentService(string baseDir) => _baseDir = baseDir;

    // ── Documents ──

    public async Task<string> ReadDocumentAsync(string path)
    {
        var fp = Path.GetFullPath(Path.Combine(_baseDir, path));
        if (!fp.StartsWith(Path.GetFullPath(_baseDir), StringComparison.OrdinalIgnoreCase))
            return "Error: path escapes workspace";
        return File.Exists(fp) ? await File.ReadAllTextAsync(fp) : "File not found";
    }

    public string[] ListDocuments(string subDir = "")
    {
        var dir = Path.Combine(_baseDir, subDir);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly).Select(f => Path.GetFileName(f)!).ToArray()
            : [];
    }

    // ── Prompts ──

    public string? GetPrompt(string name)
    {
        var path = Path.Combine(_baseDir, "prompts", $"{name}.md");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public string[] ListPrompts()
    {
        var dir = Path.Combine(_baseDir, "prompts");
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.md").Select(f => Path.GetFileNameWithoutExtension(f)!).ToArray()
            : [];
    }

    // ── Memory ──

    public async Task<string[]?> GetMemoryFilesAsync()
    {
        var dir = Path.Combine(_baseDir, "memory");
        if (!Directory.Exists(dir)) return [];
        return await Task.FromResult(Directory.GetFiles(dir, "*.md")
            .Select(f => Path.GetFileNameWithoutExtension(f)!).ToArray());
    }

    public async Task<string?> ReadMemoryAsync(string name)
    {
        var path = Path.Combine(_baseDir, "memory", $"{name}.md");
        return File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
    }

    public async Task WriteMemoryAsync(string name, string content)
    {
        var dir = Path.Combine(_baseDir, "memory");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, $"{name}.md"), content);
    }
}

public static class KnowledgeServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIKnowledge(this IServiceCollection services, string? dataDir = null)
    {
        var dir = dataDir ?? Directory.GetCurrentDirectory();
        services.AddSingleton(new DocumentService(dir));
        return services;
    }
}
