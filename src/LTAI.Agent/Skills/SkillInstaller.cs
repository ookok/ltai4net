using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Skills;

/// <summary>
/// Installs Skills from remote sources (GitHub raw, URL, local path).
/// Skills are just .md files — installation is a simple download.
/// </summary>
public sealed class SkillInstaller
{
    private readonly SkillLoader _loader;
    private readonly SkillRegistry _registry;
    private readonly HttpClient _http;
    private readonly ILogger<SkillInstaller> _logger;
    private readonly string _skillsRoot;

    public SkillInstaller(
        SkillLoader loader,
        SkillRegistry registry,
        HttpClient http,
        ILogger<SkillInstaller> logger,
        string? skillsRoot = null)
    {
        _loader = loader;
        _registry = registry;
        _http = http;
        _logger = logger;
        _skillsRoot = skillsRoot ?? Path.Combine(AppContext.BaseDirectory, "skills");
    }

    public async Task<Skill?> InstallFromUrlAsync(string url, CancellationToken ct = default)
    {
        _logger.LogInformation("Installing skill from URL: {Url}", url);

        try
        {
            var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to download skill: {Status}", response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return await InstallFromContentAsync(content, url, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install skill from {Url}", url);
            return null;
        }
    }

    public async Task<Skill?> InstallFromGitHubAsync(string repoPath, CancellationToken ct = default)
    {
        var url = $"https://raw.githubusercontent.com/{repoPath}/main";
        _logger.LogInformation("Installing skill from GitHub: {Repo}", repoPath);

        if (!repoPath.Contains('/'))
        {
            var listUrl = $"https://api.github.com/repos/{repoPath}/contents/skills";
            return await InstallFromUrlAsync(listUrl, ct).ConfigureAwait(false);
        }

        return await InstallFromUrlAsync(url, ct).ConfigureAwait(false);
    }

    public async Task<Skill?> InstallFromLocalAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning("Skill file not found: {Path}", path);
            return null;
        }

        var skill = await _loader.LoadAsync(path, ct).ConfigureAwait(false);
        if (skill == null) return null;

        var destDir = Path.Combine(_skillsRoot, skill.LayerDir);
        Directory.CreateDirectory(destDir);

        var destFile = Path.Combine(destDir, Path.GetFileName(path));
        File.Copy(path, destFile, overwrite: true);

        _logger.LogInformation("Installed skill {Name} to {Path}", skill.Name, destFile);
        return skill;
    }

    private async Task<Skill?> InstallFromContentAsync(string content, string source, CancellationToken ct)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ltai_skill_{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(tempFile, content, ct).ConfigureAwait(false);

            var skill = await _loader.LoadAsync(tempFile, ct).ConfigureAwait(false);
            if (skill == null) return null;

            var destDir = Path.Combine(_skillsRoot, skill.LayerDir);
            Directory.CreateDirectory(destDir);

            var destFile = Path.Combine(destDir, $"{skill.Name}.md");
            await File.WriteAllTextAsync(destFile, content, ct).ConfigureAwait(false);

            _logger.LogInformation("Installed skill {Name} from {Source}", skill.Name, source);
            return skill;
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }
}
