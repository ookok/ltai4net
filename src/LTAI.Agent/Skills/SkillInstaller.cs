using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LTAI.Knowledge.Core;
using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Skills;

public sealed class SkillInstaller
{
    private readonly SkillLoader _loader;
    private readonly SkillRegistry _registry;
    private readonly HttpClient _http;
    private readonly ILogger<SkillInstaller> _logger;
    private readonly string _skillsRoot;

    private static readonly JsonSerializerOptions GitHubApiJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

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
        _skillsRoot = skillsRoot ?? OptionService.Get("paths.skills") ?? Path.Combine(AppContext.BaseDirectory, "skills");
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

    public async Task<Skill?> InstallFromGitHubAsync(string repoPath, string? branch = null,
        string? token = null, CancellationToken ct = default)
    {
        _logger.LogInformation("Installing skill from GitHub: {Repo} (branch: {Branch})", repoPath, branch ?? "main");

        var parts = repoPath.Split('/');
        if (parts.Length < 2)
        {
            _logger.LogWarning("Invalid GitHub repo path: {Path}", repoPath);
            return null;
        }

        var owner = parts[0];
        var repo = parts[1];
        var subPath = parts.Length > 2 ? string.Join("/", parts.Skip(2)) : "skills";
        var targetBranch = branch ?? "main";

        var apiUrl = $"https://api.github.com/repos/{owner}/{repo}/contents/{subPath}?ref={targetBranch}";
        var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.Add("User-Agent", "LTAI-SkillInstaller");
        request.Headers.Add("Accept", "application/vnd.github.v3+json");
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        try
        {
            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub API request failed: {Status} for {Repo}/{Path}",
                    response.StatusCode, repoPath, subPath);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var items = JsonSerializer.Deserialize<List<GitHubContentItem>>(json, GitHubApiJsonOpts);
            if (items == null || items.Count == 0)
            {
                _logger.LogWarning("No content found in GitHub repo {Repo}/{Path}", repoPath, subPath);
                return null;
            }

            var mdItems = items.Where(i => i.Type == "file" && i.Name.EndsWith(".md",
                StringComparison.OrdinalIgnoreCase) && !i.Name.EndsWith(".meta.json",
                StringComparison.OrdinalIgnoreCase)).ToList();

            if (mdItems.Count == 0)
            {
                _logger.LogWarning("No .md skill files found in {Repo}/{Path}", repoPath, subPath);
                return null;
            }

            Skill? firstInstalled = null;
            foreach (var item in mdItems)
            {
                var downloadRequest = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.github.com/repos/{owner}/{repo}/contents/{subPath}/{item.Name}?ref={targetBranch}");
                downloadRequest.Headers.Add("User-Agent", "LTAI-SkillInstaller");
                downloadRequest.Headers.Add("Accept", "application/vnd.github.v3.raw");
                if (!string.IsNullOrEmpty(token))
                    downloadRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                var downloadResponse = await _http.SendAsync(downloadRequest, ct).ConfigureAwait(false);
                if (!downloadResponse.IsSuccessStatusCode) continue;

                var content = await downloadResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(content)) continue;

                var skill = await InstallFromContentAsync(content,
                    $"github:{owner}/{repo}/{subPath}/{item.Name}", ct).ConfigureAwait(false);
                if (skill != null && firstInstalled == null)
                    firstInstalled = skill;
            }

            return firstInstalled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install skills from GitHub {Repo}", repoPath);
            return null;
        }
    }

    private sealed record GitHubContentItem
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public string? DownloadUrl { get; init; }
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

            // Compute content hash for integrity verification
            var contentHash = ComputeSha256(content);

            // If the skill declares a content_hash, verify it matches the actual content
            if (!string.IsNullOrEmpty(skill.ContentHash))
            {
                if (!string.Equals(skill.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError(
                        "Content hash mismatch for skill '{Skill}' from {Source}: declared={Declared} actual={Actual}",
                        skill.Name, source, skill.ContentHash, contentHash);
                    return null;
                }
                _logger.LogInformation("Content hash verified for skill '{Skill}': {Hash}", skill.Name, contentHash);
            }
            else
            {
                // Auto-populate hash for downloaded skills that don't declare one
                skill = skill with { ContentHash = contentHash };
                _logger.LogInformation("Content hash computed for skill '{Skill}': {Hash}", skill.Name, contentHash);
            }

            var destDir = Path.Combine(_skillsRoot, skill.LayerDir);
            Directory.CreateDirectory(destDir);

            var destFile = Path.Combine(destDir, $"{skill.Name}.md");
            await File.WriteAllTextAsync(destFile, content, ct).ConfigureAwait(false);

            // Write hash sidecar file for runtime verification
            var hashFile = destFile + ".sha256";
            await File.WriteAllTextAsync(hashFile, contentHash, ct).ConfigureAwait(false);

            _registry.Register(skill);

            _logger.LogInformation("Installed skill {Name} from {Source} (hash={Hash})", skill.Name, source, contentHash);
            return skill;
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    /// <summary>Compute SHA256 content hash for integrity verification.</summary>
    private static string ComputeSha256(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Verify a file's content matches its .sha256 sidecar or embedded hash.</summary>
    public static bool VerifyFileIntegrity(string filePath)
    {
        if (!File.Exists(filePath)) return false;

        // Check sidecar .sha256 file first
        var hashFilePath = filePath + ".sha256";
        if (File.Exists(hashFilePath))
        {
            var expectedHash = File.ReadAllText(hashFilePath).Trim();
            var content = File.ReadAllText(filePath);
            var actualHash = ComputeSha256(content);
            return string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase);
        }

        return true; // No sidecar = legacy file, skip
    }
}
