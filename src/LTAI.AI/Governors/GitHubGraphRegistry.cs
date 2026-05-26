using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ==================== GitHub 图谱注册表 ====================

public record GitHubGraphConfig
{
    public string Owner { get; init; } = "";
    public string Repository { get; init; } = "ltai-graphs";
    public string? Token { get; init; }
    public string ApiBaseUrl { get; init; } = "https://api.github.com";
    public int MaxDownloadSizeMB { get; init; } = 200;
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public int MaxRetries { get; init; } = 3;
}

public record PublishedGraphInfo
{
    public string GraphId { get; init; } = "";
    public string Domain { get; init; } = "";
    public string Version { get; init; } = "";
    public string ReleaseUrl { get; init; } = "";
    public long SizeBytes { get; init; }
    public int EntityCount { get; init; }
    public int TripletCount { get; init; }
    public DateTime PublishedAt { get; init; }
}

public sealed class GitHubGraphRegistry : IDisposable
{
    private readonly GitHubGraphConfig _config;
    private readonly HttpClient _httpClient;
    private readonly GraphPackageManager _packageManager;
    private readonly ILogger<GitHubGraphRegistry> _logger;
    private readonly string _cacheDirectory;

    public GitHubGraphRegistry(
        GitHubGraphConfig config,
        GraphPackageManager packageManager,
        ILogger<GitHubGraphRegistry>? logger = null)
    {
        _config = config;
        _packageManager = packageManager;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubGraphRegistry>.Instance;
        _cacheDirectory = Path.Combine(AppContext.BaseDirectory, "synaptic", "graph_cache");
        Directory.CreateDirectory(_cacheDirectory);

        _httpClient = new HttpClient(new GitHubAuthHandler(_config.Token ?? "", new Uri(_config.ApiBaseUrl).Host))
        {
            BaseAddress = new Uri(_config.ApiBaseUrl),
            Timeout = _config.DownloadTimeout
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LTAI-GraphAI/1.0");
    }

    /// <summary>
    /// 发布知识图谱到 GitHub Release
    /// </summary>
    public async Task<PublishedGraphInfo?> PublishGraphAsync(
        string packagePath,
        GraphPackageManifest manifest,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.Token))
        {
            _logger.LogWarning("Cannot publish: GitHub token not configured");
            return null;
        }

        try
        {
            // 1. 创建 Release
            var releaseData = new
            {
                tag_name = $"{manifest.GraphId}-v{manifest.Version}",
                name = $"{manifest.Domain} Knowledge Graph v{manifest.Version}",
                body = BuildReleaseDescription(manifest),
                draft = false,
                prerelease = manifest.Version.Contains("beta") || manifest.Version.Contains("alpha")
            };

            var releaseResponse = await PostJsonAsync<GitHubRelease>(
                $"/repos/{_config.Owner}/{_config.Repository}/releases",
                releaseData, ct);

            if (releaseResponse == null)
            {
                _logger.LogWarning("Failed to create GitHub release");
                return null;
            }

            // 2. 上传图谱包作为 Release Asset
            await UploadReleaseAssetAsync(releaseResponse, packagePath, manifest, ct).ConfigureAwait(false);

            var publishedInfo = new PublishedGraphInfo
            {
                GraphId = manifest.GraphId,
                Domain = manifest.Domain,
                Version = manifest.Version,
                ReleaseUrl = $"https://github.com/{_config.Owner}/{_config.Repository}/releases/tag/{releaseResponse.TagName}",
                SizeBytes = manifest.TotalSizeBytes,
                EntityCount = manifest.EntityCount,
                TripletCount = manifest.TripletCount,
                PublishedAt = DateTime.UtcNow
            };

            _logger.LogInformation(
                "Graph published to GitHub: id={Id} domain={Domain} entities={Entities} triplets={Triplets} url={Url}",
                manifest.GraphId, manifest.Domain, manifest.EntityCount, manifest.TripletCount, publishedInfo.ReleaseUrl);

            return publishedInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish graph to GitHub: {Id}", manifest.GraphId);
            return null;
        }
    }

    /// <summary>
    /// 从 GitHub 下载知识图谱
    /// </summary>
    public async Task<GraphPackageInfo?> DownloadGraphAsync(
        string graphId,
        string version = "latest",
        CancellationToken ct = default)
    {
        try
        {
            // 1. 查找 Release
            var release = await FindReleaseAsync(graphId, version, ct).ConfigureAwait(false);
            if (release == null)
            {
                _logger.LogWarning("Release not found: id={Id} version={Version}", graphId, version);
                return null;
            }

            // 2. 查找 .graphpackage 资产
            var packageAsset = release.Assets
                .FirstOrDefault(a => a.Name.EndsWith(".graphpackage"));
            if (packageAsset == null)
            {
                _logger.LogWarning("No .graphpackage asset found in release: {Tag}", release.TagName);
                return null;
            }

            // 3. 验证大小限制
            if (packageAsset.Size > _config.MaxDownloadSizeMB * 1024L * 1024L)
            {
                _logger.LogWarning(
                    "Package too large: {SizeMB:F1}MB > {MaxMB}MB",
                    packageAsset.Size / 1024.0 / 1024.0,
                    _config.MaxDownloadSizeMB);
                return null;
            }

            // 4. 下载
            var localPath = Path.Combine(_cacheDirectory, packageAsset.Name);
            await DownloadAssetAsync(packageAsset, localPath, ct).ConfigureAwait(false);

            // 5. 安装
            var packageInfo = await _packageManager.InstallPackageAsync(localPath, ct).ConfigureAwait(false);

            if (packageInfo != null)
            {
                _logger.LogInformation(
                    "Graph downloaded and installed: id={Id} domain={Domain} entities={Entities} triplets={Triplets} size={SizeKB:F1}KB",
                    packageInfo.GraphId, packageInfo.Domain,
                    packageInfo.Manifest.EntityCount, packageInfo.Manifest.TripletCount,
                    packageInfo.Manifest.TotalSizeBytes / 1024.0);
            }

            return packageInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download graph: {Id}", graphId);
            return null;
        }
    }

    /// <summary>
    /// 搜索可用的知识图谱
    /// </summary>
    public async Task<List<PublishedGraphInfo>> SearchGraphsAsync(
        string? domain = null,
        string? tag = null,
        int maxResults = 20,
        CancellationToken ct = default)
    {
        var results = new List<PublishedGraphInfo>();

        try
        {
            // 构建搜索查询
            var query = $"repo:{_config.Owner}/{_config.Repository} topic:knowledge-graph";
            if (!string.IsNullOrEmpty(domain))
            {
                query += $" topic:{domain.ToLowerInvariant()}";
            }
            if (!string.IsNullOrEmpty(tag))
            {
                query += $" topic:{tag.ToLowerInvariant()}";
            }

            var searchUrl = $"/search/repositories?q={Uri.EscapeDataString(query)}&per_page={maxResults}";
            var searchResult = await GetJsonAsync<GitHubSearchResult>(searchUrl, ct).ConfigureAwait(false);

            if (searchResult == null) return results;

            foreach (var repo in searchResult.Items)
            {
                // 获取该仓库的 Releases
                var releasesUrl = $"/repos/{repo.FullName}/releases?per_page=5";
                var releases = await GetJsonAsync<List<GitHubRelease>>(releasesUrl, ct).ConfigureAwait(false);

                if (releases != null)
                {
                    foreach (var release in releases)
                    {
                        var packageAsset = release.Assets
                            .FirstOrDefault(a => a.Name.EndsWith(".graphpackage"));
                        if (packageAsset != null)
                        {
                            var graphId = ExtractGraphIdFromRelease(release);
                            var domainName = ExtractDomainFromRelease(release);

                            results.Add(new PublishedGraphInfo
                            {
                                GraphId = graphId,
                                Domain = domainName,
                                Version = release.TagName,
                                ReleaseUrl = packageAsset.DownloadUrl,
                                SizeBytes = packageAsset.Size,
                                EntityCount = 0,  // 需要从清单读取
                                TripletCount = 0,
                                PublishedAt = release.CreatedAt
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search graphs on GitHub");
        }

        return results.OrderByDescending(r => r.PublishedAt).Take(maxResults).ToList();
    }

    /// <summary>
    /// 批量下载依赖图谱 (级联加载)
    /// </summary>
    public async Task<List<GraphPackageInfo>> DownloadDependenciesAsync(
        List<GraphDependency> dependencies,
        CancellationToken ct = default)
    {
        var installed = new List<GraphPackageInfo>();

        // 按加载顺序排序
        var sortedDeps = dependencies.OrderBy(d => d.LoadOrder).ToList();

        foreach (var dep in sortedDeps)
        {
            if (ct.IsCancellationRequested) break;

            // 检查是否已安装
            var existing = _packageManager.GetPackage(dep.GraphId);
            if (existing != null)
            {
                _logger.LogDebug("Dependency already installed: {Id}", dep.GraphId);
                continue;
            }

            // 下载依赖
            var packageInfo = await DownloadGraphAsync(dep.GraphId, dep.MinVersion, ct).ConfigureAwait(false);
            if (packageInfo != null)
            {
                installed.Add(packageInfo);
                _logger.LogInformation(
                    "Dependency downloaded: id={Id} domain={Domain} order={Order}",
                    dep.GraphId, dep.Domain, dep.LoadOrder);
            }
            else if (dep.IsRequired)
            {
                _logger.LogWarning(
                    "Failed to download required dependency: {Id}",
                    dep.GraphId);
                throw new InvalidOperationException(
                    $"Required dependency not available: {dep.GraphId}");
            }
        }

        return installed;
    }

    // ==================== 内部方法 ====================

    private async Task<GitHubRelease?> FindReleaseAsync(
        string graphId, string version, CancellationToken ct)
    {
        if (version == "latest")
        {
            return await GetJsonAsync<GitHubRelease>(
                $"/repos/{_config.Owner}/{_config.Repository}/releases/latest", ct);
        }

        // 查找特定版本
        var releases = await GetJsonAsync<List<GitHubRelease>>(
            $"/repos/{_config.Owner}/{_config.Repository}/releases?per_page=100", ct);

        return releases?.FirstOrDefault(r =>
            r.TagName == $"{graphId}-v{version}" ||
            r.Name.Contains(graphId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task DownloadAssetAsync(
        GitHubAsset asset, string localPath, CancellationToken ct)
    {
        for (var retry = 0; retry < _config.MaxRetries; retry++)
        {
            try
            {
                using var response = await _httpClient.GetAsync(asset.DownloadUrl, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var fileStream = File.Create(localPath);
                await response.Content.CopyToAsync(fileStream, ct).ConfigureAwait(false);

                _logger.LogInformation(
                    "Asset downloaded: {Name} size={SizeKB:F1}KB",
                    asset.Name, new FileInfo(localPath).Length / 1024.0);
                return;
            }
            catch (Exception ex) when (retry < _config.MaxRetries - 1)
            {
                _logger.LogWarning(ex, "Download failed, retry {Retry}/{Max}", retry + 1, _config.MaxRetries);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry)), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<string?> UploadReleaseAssetAsync(
        GitHubRelease release, string packagePath, GraphPackageManifest manifest, CancellationToken ct)
    {
        var uploadUrl = $"https://uploads.github.com/repos/{_config.Owner}/{_config.Repository}/releases/{release.Id}/assets?name={Uri.EscapeDataString(Path.GetFileName(packagePath))}";

        using var content = new StreamContent(File.OpenRead(packagePath));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = new FileInfo(packagePath).Length;

        using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl)
        {
            Content = content
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation(
            "Release asset uploaded: {Name} size={SizeKB:F1}KB",
            Path.GetFileName(packagePath), new FileInfo(packagePath).Length / 1024.0);

        return uploadUrl;
    }

    private static string BuildReleaseDescription(GraphPackageManifest manifest)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {manifest.Domain} Knowledge Graph");
        sb.AppendLine();
        sb.AppendLine(manifest.Description);
        sb.AppendLine();
        sb.AppendLine("## Statistics");
        sb.AppendLine($"- **Graph ID**: {manifest.GraphId}");
        sb.AppendLine($"- **Version**: {manifest.Version}");
        sb.AppendLine($"- **Entities**: {manifest.EntityCount:N0}");
        sb.AppendLine($"- **Triplets**: {manifest.TripletCount:N0}");
        sb.AppendLine($"- **Relation Types**: {string.Join(", ", manifest.RelationTypes)}");
        sb.AppendLine($"- **Total Size**: {manifest.TotalSizeBytes / 1024.0:F1} KB");
        sb.AppendLine($"- **Compression**: {manifest.Compression}");
        sb.AppendLine($"- **Shards**: {manifest.ShardCount}");
        sb.AppendLine();
        sb.AppendLine("## Dependencies");
        foreach (var dep in manifest.Dependencies)
        {
            sb.AppendLine($"- {dep.Domain} (>= {dep.MinVersion})");
        }
        sb.AppendLine();
        sb.AppendLine($"## License: {manifest.License}");
        return sb.ToString();
    }

    private static string ExtractGraphIdFromRelease(GitHubRelease release)
    {
        var parts = release.TagName.Split('-');
        return parts.Length > 0 ? parts[0] : release.TagName;
    }

    private static string ExtractDomainFromRelease(GitHubRelease release)
    {
        var parts = release.Name.Split(' ');
        return parts.Length > 0 ? parts[0] : "unknown";
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<T>(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET request failed: {Url}", url);
            return default;
        }
    }

    private async Task<T?> PostJsonAsync<T>(string url, object data, CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(url, data, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST request failed: {Url}", url);
            return default;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _logger.LogInformation("GitHubGraphRegistry disposed");
    }
}
