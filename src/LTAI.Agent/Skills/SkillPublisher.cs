using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LTAI.Knowledge.Core;
using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Skills;

public sealed class SkillPublisher : ISkillExchangeProvider
{
    private readonly SkillRegistry _registry;
    private readonly SkillLoader _loader;
    private readonly HttpClient _http;
    private readonly ILogger<SkillPublisher> _logger;
    private readonly string _skillsRoot;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly HashSet<string> DangerousGitArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm -rf", "reset --hard", "clean -f", "push --force", "--force",
        "rebase", "filter-branch", "gc --prune"
    };

    private const int GitTimeoutMs = 60_000;

    public SkillPublisher(
        SkillRegistry registry,
        SkillLoader loader,
        HttpClient http,
        ILogger<SkillPublisher> logger,
        string? skillsRoot = null)
    {
        _registry = registry;
        _loader = loader;
        _http = http;
        _logger = logger;
        _skillsRoot = skillsRoot ?? OptionService.Get("paths.skills") ?? Path.Combine(AppContext.BaseDirectory, "skills");
    }

    public async Task<(bool Success, string? CommitSha)> PublishSkillToGitAsync(
        string skillName, string repoUrl, string? branch = null, string? token = null,
        CancellationToken ct = default)
    {
        var skill = _registry.Get(skillName);
        if (skill == null)
        {
            _logger.LogWarning("Skill {Name} not found for publishing", skillName);
            return (false, null);
        }

        if (skill.SourceFile == null)
        {
            _logger.LogWarning("Skill {Name} has no source file", skillName);
            return (false, null);
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"ltai_git_publish_{Guid.NewGuid():N}");
        try
        {
            var cloneUrl = ApplyToken(repoUrl, token);
            var targetBranch = branch ?? "main";

            await RunGitAsync($"clone --branch {targetBranch} --depth 1 {cloneUrl} .", tempDir, ct);

            var destSkillsDir = Path.Combine(tempDir, "skills");
            Directory.CreateDirectory(destSkillsDir);

            var mdFile = skill.SourceFile;
            var destMd = Path.Combine(destSkillsDir, Path.GetFileName(mdFile));
            File.Copy(mdFile, destMd, overwrite: true);

            var metaFile = mdFile + ".meta.json";
            if (File.Exists(metaFile))
            {
                var destMeta = Path.Combine(destSkillsDir, Path.GetFileName(metaFile));
                File.Copy(metaFile, destMeta, overwrite: true);
            }

            var versionsDir = Path.Combine(Path.GetDirectoryName(mdFile) ?? throw new InvalidOperationException("Skill source file has no parent directory"), "versions");
            if (Directory.Exists(versionsDir))
            {
                var destVersions = Path.Combine(destSkillsDir, "versions");
                Directory.CreateDirectory(destVersions);
                foreach (var vf in Directory.GetFiles(versionsDir))
                {
                    var destVf = Path.Combine(destVersions, Path.GetFileName(vf));
                    File.Copy(vf, destVf, overwrite: true);
                }
            }

            await RunGitAsync("add skills/", tempDir, ct);
            await RunGitAsync($"commit -m \"publish: {skill.Name} v{skill.Version}\"", tempDir, ct);
            await RunGitAsync($"push origin {targetBranch}", tempDir, ct);

            var sha = await RunGitAsync("rev-parse HEAD", tempDir, ct);
            var commitSha = sha.Trim();
            _logger.LogInformation("Published skill {Name} to {Repo}, commit {Sha}", skillName, repoUrl, commitSha);
            return (true, commitSha);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish skill {Name} to {Repo}", skillName, repoUrl);
            return (false, null);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* intentional: cleanup may fail */ }
        }
    }

    public async Task<(int Count, string[] SkillNames)> PullSkillsFromGitAsync(
        string repoUrl, string? branch = null, string? token = null,
        CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ltai_git_pull_{Guid.NewGuid():N}");
        var importedNames = new List<string>();

        try
        {
            var cloneUrl = ApplyToken(repoUrl, token);
            var targetBranch = branch ?? "main";

            await RunGitAsync($"clone --branch {targetBranch} --depth 1 {cloneUrl} .", tempDir, ct);

            var repoSkillsDir = Path.Combine(tempDir, "skills");
            if (!Directory.Exists(repoSkillsDir))
            {
                _logger.LogWarning("No skills/ directory in repo {Repo}", repoUrl);
                return (0, Array.Empty<string>());
            }

            var mdFiles = Directory.GetFiles(repoSkillsDir, "*.md", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase));

            var count = 0;
            foreach (var file in mdFiles)
            {
                var skill = await _loader.LoadAsync(file, ct).ConfigureAwait(false);
                if (skill == null) continue;

                var relativePath = Path.GetRelativePath(repoSkillsDir, file);
                var destDir = Path.GetDirectoryName(Path.Combine(_skillsRoot, relativePath));
                if (destDir == null) continue;
                Directory.CreateDirectory(destDir);

                var destFile = Path.Combine(_skillsRoot, relativePath);
                File.Copy(file, destFile, overwrite: true);

                var metaFile = file + ".meta.json";
                if (File.Exists(metaFile))
                {
                    var destMeta = destFile + ".meta.json";
                    File.Copy(metaFile, destMeta, overwrite: true);
                }

                var versionsSrcDir = Path.Combine(Path.GetDirectoryName(file) ?? throw new InvalidOperationException("Skill file has no parent directory"), "versions");
                if (Directory.Exists(versionsSrcDir))
                {
                    var versionsDestDir = Path.Combine(Path.GetDirectoryName(destFile) ?? throw new InvalidOperationException("Destination file has no parent directory"), "versions");
                    Directory.CreateDirectory(versionsDestDir);
                    foreach (var vf in Directory.GetFiles(versionsSrcDir))
                    {
                        var destVf = Path.Combine(versionsDestDir, Path.GetFileName(vf));
                        File.Copy(vf, destVf, overwrite: true);
                    }
                }

                skill = skill with { SourceFile = destFile };
                _registry.Register(skill);
                importedNames.Add(skill.Name);
                count++;
                _logger.LogInformation("Pulled skill {Name} from git repo {Repo}", skill.Name, repoUrl);
            }

            _logger.LogInformation("Pulled {Count} skills from git repo {Repo}", count, repoUrl);
            return (count, importedNames.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pull skills from {Repo}", repoUrl);
            return (0, Array.Empty<string>());
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* intentional: cleanup may fail */ }
        }
    }

    public async Task<string> ExportSkillPackageAsync(string skillName, string outputDir,
        CancellationToken ct = default)
    {
        var skill = _registry.Get(skillName);
        if (skill == null || skill.SourceFile == null)
            throw new InvalidOperationException($"Skill {skillName} not found or has no source file");

        Directory.CreateDirectory(outputDir);
        var zipPath = Path.Combine(outputDir, $"{skill.Name}_v{skill.Version}.zip");

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var sourceFile = skill.SourceFile;
        zip.CreateEntryFromFile(sourceFile, Path.GetFileName(sourceFile));

        var metaFile = sourceFile + ".meta.json";
        if (File.Exists(metaFile))
            zip.CreateEntryFromFile(metaFile, Path.GetFileName(metaFile));

        var versionsDir = Path.Combine(Path.GetDirectoryName(sourceFile) ?? throw new InvalidOperationException("Source file has no parent directory"), "versions");
        if (Directory.Exists(versionsDir))
        {
            foreach (var vf in Directory.GetFiles(versionsDir))
                zip.CreateEntryFromFile(vf, Path.Combine("versions", Path.GetFileName(vf)));
        }

        _logger.LogInformation("Exported skill package {Name} to {Path}", skillName, zipPath);
        return zipPath;
    }

    public async Task<Skill?> ImportSkillPackageAsync(string zipPath, CancellationToken ct = default)
    {
        if (!File.Exists(zipPath))
        {
            _logger.LogWarning("Skill package not found: {Path}", zipPath);
            return null;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"ltai_skill_import_{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(zipPath, tempDir);

            var mdFiles = Directory.GetFiles(tempDir, "*.md", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)
                            && !f.Contains(Path.DirectorySeparatorChar + "versions" + Path.DirectorySeparatorChar))
                .ToList();

            if (mdFiles.Count == 0)
            {
                _logger.LogWarning("No skill .md files found in package {Path}", zipPath);
                return null;
            }

            var skillFile = mdFiles.OrderBy(f => f).First();
            var skill = await _loader.LoadAsync(skillFile, ct).ConfigureAwait(false);
            if (skill == null) return null;

            var content = await File.ReadAllTextAsync(skillFile, ct).ConfigureAwait(false);
            var computedHash = ComputeSha256(content);

            var existing = _registry.Get(skill.Name);
            if (existing?.ContentHash != null && !string.Equals(computedHash, existing.ContentHash, StringComparison.OrdinalIgnoreCase))
                _logger.LogWarning("Content hash mismatch importing {Name}: existing={Existing} imported={Imported}",
                    skill.Name, existing.ContentHash, computedHash);

            var destDir = Path.Combine(_skillsRoot, skill.LayerDir);
            Directory.CreateDirectory(destDir);

            var destFile = Path.Combine(destDir, Path.GetFileName(skillFile));
            File.Copy(skillFile, destFile, overwrite: true);

            var metaSrc = skillFile + ".meta.json";
            if (File.Exists(metaSrc))
            {
                var metaDest = destFile + ".meta.json";
                File.Copy(metaSrc, metaDest, overwrite: true);
            }

            var versionsSrc = Path.Combine(tempDir, "versions");
            if (Directory.Exists(versionsSrc))
            {
                var versionsDest = Path.Combine(destDir, "versions");
                Directory.CreateDirectory(versionsDest);
                foreach (var vf in Directory.GetFiles(versionsSrc))
                {
                    var destVf = Path.Combine(versionsDest, Path.GetFileName(vf));
                    File.Copy(vf, destVf, overwrite: true);
                }
            }

            skill = skill with { SourceFile = destFile, ContentHash = computedHash };
            _registry.Register(skill);
            _logger.LogInformation("Imported skill {Name} from package {Path}", skill.Name, zipPath);
            return skill;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import skill package {Path}", zipPath);
            return null;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* intentional: cleanup may fail */ }
        }
    }

    public async Task<List<(string Name, string Version, string Domain, string Description)>> GetPeerSkillsAsync(
        string peerAddress, CancellationToken ct = default)
    {
        try
        {
            var url = $"http://{peerAddress}/p2p/skills";
            var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<List<SkillManifestEntry>>(json, JsonOpts);
            return manifest?.Select(m => (m.Name, m.Version, m.Domain, m.Description)).ToList() ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get peer skills from {Address}", peerAddress);
            return new();
        }
    }

    public async Task<(int Imported, int Skipped, int Errors)> SyncWithPeerAsync(
        string peerAddress, CancellationToken ct = default)
    {
        var imported = 0;
        var skipped = 0;
        var errors = 0;

        try
        {
            var peerSkills = await GetPeerSkillsAsync(peerAddress, ct).ConfigureAwait(false);
            foreach (var (name, version, domain, description) in peerSkills)
            {
                try
                {
                    var local = _registry.Get(name);
                    if (local != null && !IsVersionNewer(version, local.Version))
                    {
                        skipped++;
                        continue;
                    }

                    var url = $"http://{peerAddress}/p2p/skills/{Uri.EscapeDataString(name)}/download";
                    var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        errors++;
                        continue;
                    }

                    var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var computedHash = ComputeSha256(content);

                    if (local != null && local.ContentHash != null)
                    {
                        if (!string.Equals(computedHash, local.ContentHash, StringComparison.OrdinalIgnoreCase))
                            _logger.LogWarning("Content hash mismatch for {Name}: existing={Existing} remote={Remote}",
                                name, local.ContentHash, computedHash);
                    }

                    var tempFile = Path.Combine(Path.GetTempPath(), $"ltai_peer_{Guid.NewGuid():N}.md");
                    try
                    {
                        await File.WriteAllTextAsync(tempFile, content, ct).ConfigureAwait(false);
                        var skill = await _loader.LoadAsync(tempFile, ct).ConfigureAwait(false);
                        if (skill == null) { errors++; continue; }

                        var destDir = Path.Combine(_skillsRoot, skill.LayerDir);
                        Directory.CreateDirectory(destDir);
                        var destFile = Path.Combine(destDir, $"{skill.Name}.md");
                        await File.WriteAllTextAsync(destFile, content, ct).ConfigureAwait(false);

                        skill = skill with { SourceFile = destFile, ContentHash = computedHash };
                        _registry.Register(skill);
                        imported++;
                        _logger.LogInformation("Synced skill {Name} v{Version} hash={Hash} from peer {Peer}",
                            skill.Name, skill.Version, computedHash[..8], peerAddress);
                    }
                    finally
                    {
                        try { File.Delete(tempFile); } catch { /* intentional: cleanup may fail */ }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to sync skill {Name} from peer {Peer}", name, peerAddress);
                    errors++;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync with peer {Peer}", peerAddress);
            errors++;
        }

        return (imported, skipped, errors);
    }

    public async Task<List<(string Name, string Version, string Domain, string Description)>> GetLocalSkillManifestAsync(
        CancellationToken ct)
    {
        await Task.CompletedTask;
        return _registry.All.Values
            .Select(s => (s.Name, s.Version, s.Domain, s.Description ?? ""))
            .ToList();
    }

    public async Task<string?> GetSkillContentAsync(string skillName, CancellationToken ct)
    {
        var skill = _registry.Get(skillName);
        if (skill?.SourceFile == null || !File.Exists(skill.SourceFile))
            return null;

        return await File.ReadAllTextAsync(skill.SourceFile, ct).ConfigureAwait(false);
    }

    private static string ApplyToken(string repoUrl, string? token)
    {
        if (string.IsNullOrEmpty(token)) return repoUrl;
        if (repoUrl.StartsWith("https://") && !repoUrl.Contains("@"))
        {
            var uri = new Uri(repoUrl);
            return $"https://{token}@{uri.Host}{uri.AbsolutePath}";
        }
        return repoUrl;
    }

    private static async Task<string> RunGitAsync(string args, string workDir, CancellationToken ct)
    {
        var sanitized = args.Trim();
        foreach (var dangerous in DangerousGitArgs)
            if (sanitized.Contains(dangerous, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Dangerous git command blocked: '{sanitized}' matched pattern '{dangerous}'");

        var processArgs = sanitized;
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = processArgs,
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            }
        };

        proc.Start();
        using var timeoutCts = new CancellationTokenSource(GitTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await proc.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* intentional: cleanup may fail */ }
            throw new TimeoutException($"Git command timed out after {GitTimeoutMs}ms: git {sanitized}");
        }

        var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Git command failed (exit {proc.ExitCode}): git {sanitized}\n{stderr}");

        return stdout;
    }

    private static bool IsVersionNewer(string remoteVersion, string localVersion)
    {
        if (Version.TryParse(remoteVersion, out var r) && Version.TryParse(localVersion, out var l))
            return r > l;
        return string.Compare(remoteVersion, localVersion, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static string ComputeSha256(string content)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hashBytes);
    }

    private sealed record SkillManifestEntry
    {
        public string Name { get; init; } = "";
        public string Version { get; init; } = "";
        public string Domain { get; init; } = "";
        public string Description { get; init; } = "";
    }
}
