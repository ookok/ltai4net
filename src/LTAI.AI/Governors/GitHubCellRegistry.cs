using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ==================== GitHub API 数据模型 ====================

public record GitHubRelease
{
    [JsonPropertyName("id")]
    public long Id { get; init; }
    
    [JsonPropertyName("tag_name")]
    public string TagName { get; init; } = "";
    
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
    
    [JsonPropertyName("body")]
    public string Body { get; init; } = "";
    
    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; }
    
    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; init; } = new();
}

public record GitHubAsset
{
    [JsonPropertyName("id")]
    public long Id { get; init; }
    
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";
    
    [JsonPropertyName("size")]
    public long Size { get; init; }
    
    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; init; } = "";
    
    [JsonPropertyName("content_type")]
    public string ContentType { get; init; } = "";
}

public record GitHubSearchResult
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; init; }
    
    [JsonPropertyName("items")]
    public List<GitHubRepoItem> Items { get; init; } = new();
}

public record GitHubRepoItem
{
    [JsonPropertyName("full_name")]
    public string FullName { get; init; } = "";
    
    [JsonPropertyName("description")]
    public string Description { get; init; } = "";
    
    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; init; } = "";
    
    [JsonPropertyName("stargazers_count")]
    public int StargazersCount { get; init; }
}

// ==================== GitHub 细胞注册表 ====================

public record GitHubCellConfig
{
    public string Owner { get; init; } = "";  // GitHub 用户名或组织
    public string Repository { get; init; } = "ltai-cells";  // 仓库名
    public string? Token { get; init; }  // Personal Access Token (可选，用于上传)
    public string ApiBaseUrl { get; init; } = "https://api.github.com";
    public int MaxDownloadSizeMB { get; init; } = 100;  // 最大下载大小
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public int MaxRetries { get; init; } = 3;
}

public record PublishedCellInfo
{
    public string CellId { get; init; } = "";
    public string Domain { get; init; } = "";
    public string Version { get; init; } = "";
    public string ReleaseUrl { get; init; } = "";
    public long SizeBytes { get; init; }
    public int DownloadCount { get; init; }
    public DateTime PublishedAt { get; init; }
}

public sealed class GitHubCellRegistry : IDisposable
{
    private readonly GitHubCellConfig _config;
    private readonly HttpClient _httpClient;
    private readonly CellPackageManager _packageManager;
    private readonly ILogger<GitHubCellRegistry> _logger;
    private readonly string _cacheDirectory;

    public GitHubCellRegistry(
        GitHubCellConfig config,
        CellPackageManager packageManager,
        ILogger<GitHubCellRegistry>? logger = null)
    {
        _config = config;
        _packageManager = packageManager;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubCellRegistry>.Instance;
        _cacheDirectory = Path.Combine(AppContext.BaseDirectory, "synaptic", "github_cache");
        Directory.CreateDirectory(_cacheDirectory);

        _httpClient = new HttpClient(new GitHubAuthHandler(_config.Token ?? "", new Uri(_config.ApiBaseUrl).Host))
        {
            BaseAddress = new Uri(_config.ApiBaseUrl),
            Timeout = _config.DownloadTimeout
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LTAI-CellAI/1.0");
    }

    /// <summary>
    /// 发布细胞 AI 到 GitHub Release
    /// </summary>
    public async Task<PublishedCellInfo?> PublishCellAsync(
        string packagePath,
        CellPackageManifest manifest,
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
                tag_name = $"{manifest.CellId}-v{manifest.Version}",
                name = $"{manifest.Domain} Cell v{manifest.Version}",
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

            // 2. 上传细胞包作为 Release Asset
            var uploadUrl = releaseResponse.Assets.Count > 0
                ? releaseResponse.Assets[0].DownloadUrl  // 简化处理
                : "";

            var assetPath = await UploadReleaseAssetAsync(
                releaseResponse, packagePath, manifest, ct).ConfigureAwait(false);

            var publishedInfo = new PublishedCellInfo
            {
                CellId = manifest.CellId,
                Domain = manifest.Domain,
                Version = manifest.Version,
                ReleaseUrl = $"https://github.com/{_config.Owner}/{_config.Repository}/releases/tag/{releaseResponse.TagName}",
                SizeBytes = manifest.TotalSizeBytes,
                DownloadCount = 0,
                PublishedAt = DateTime.UtcNow
            };

            _logger.LogInformation(
                "Cell published to GitHub: id={Id} domain={Domain} url={Url}",
                manifest.CellId, manifest.Domain, publishedInfo.ReleaseUrl);

            return publishedInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish cell to GitHub: {Id}", manifest.CellId);
            return null;
        }
    }

    /// <summary>
    /// 从 GitHub 下载细胞 AI
    /// </summary>
    public async Task<CellPackageInfo?> DownloadCellAsync(
        string cellId,
        string version = "latest",
        CancellationToken ct = default)
    {
        try
        {
            // 1. 查找 Release
            var release = await FindReleaseAsync(cellId, version, ct).ConfigureAwait(false);
            if (release == null)
            {
                _logger.LogWarning("Release not found: id={Id} version={Version}", cellId, version);
                return null;
            }

            // 2. 查找 .cellpackage 资产
            var packageAsset = release.Assets
                .FirstOrDefault(a => a.Name.EndsWith(".cellpackage"));
            if (packageAsset == null)
            {
                _logger.LogWarning("No .cellpackage asset found in release: {Tag}", release.TagName);
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
                    "Cell downloaded and installed: id={Id} domain={Domain} size={SizeKB:F1}KB",
                    packageInfo.CellId, packageInfo.Domain,
                    packageInfo.Manifest.TotalSizeBytes / 1024.0);
            }

            return packageInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download cell: {Id}", cellId);
            return null;
        }
    }

    /// <summary>
    /// 搜索可用的细胞 AI
    /// </summary>
    public async Task<List<PublishedCellInfo>> SearchCellsAsync(
        string? domain = null,
        string? tag = null,
        int maxResults = 20,
        CancellationToken ct = default)
    {
        var results = new List<PublishedCellInfo>();

        try
        {
            // 构建搜索查询
            var query = $"repo:{_config.Owner}/{_config.Repository} topic:cell-ai";
            if (!string.IsNullOrEmpty(domain))
            {
                query += $" topic:{domain.ToLowerInvariant()}";
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
                            .FirstOrDefault(a => a.Name.EndsWith(".cellpackage"));
                        if (packageAsset != null)
                        {
                            var cellId = ExtractCellIdFromRelease(release);
                            var domainName = ExtractDomainFromRelease(release);

                            results.Add(new PublishedCellInfo
                            {
                                CellId = cellId,
                                Domain = domainName,
                                Version = release.TagName,
                                ReleaseUrl = release.Assets.Count > 0
                                    ? packageAsset.DownloadUrl
                                    : "",
                                SizeBytes = packageAsset.Size,
                                DownloadCount = 0,  // GitHub API 不直接提供下载计数
                                PublishedAt = release.CreatedAt
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search cells on GitHub");
        }

        return results.OrderByDescending(r => r.PublishedAt).Take(maxResults).ToList();
    }

    /// <summary>
    /// 批量下载依赖细胞 (级联加载)
    /// </summary>
    public async Task<List<CellPackageInfo>> DownloadDependenciesAsync(
        List<CellDependency> dependencies,
        CancellationToken ct = default)
    {
        var installed = new List<CellPackageInfo>();

        // 按加载顺序排序
        var sortedDeps = dependencies.OrderBy(d => d.LoadOrder).ToList();

        foreach (var dep in sortedDeps)
        {
            if (ct.IsCancellationRequested) break;

            // 检查是否已安装
            var existing = _packageManager.GetPackage(dep.CellId);
            if (existing != null)
            {
                _logger.LogDebug("Dependency already installed: {Id}", dep.CellId);
                continue;
            }

            // 下载依赖
            var packageInfo = await DownloadCellAsync(dep.CellId, dep.MinVersion, ct).ConfigureAwait(false);
            if (packageInfo != null)
            {
                installed.Add(packageInfo);
                _logger.LogInformation(
                    "Dependency downloaded: id={Id} domain={Domain} order={Order}",
                    dep.CellId, dep.Domain, dep.LoadOrder);
            }
            else if (dep.IsRequired)
            {
                _logger.LogWarning(
                    "Failed to download required dependency: {Id}",
                    dep.CellId);
                throw new InvalidOperationException(
                    $"Required dependency not available: {dep.CellId}");
            }
        }

        return installed;
    }

    // ==================== 内部方法 ====================

    private async Task<GitHubRelease?> FindReleaseAsync(
        string cellId, string version, CancellationToken ct)
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
            r.TagName == $"{cellId}-v{version}" ||
            r.Name.Contains(cellId, StringComparison.OrdinalIgnoreCase));
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
        GitHubRelease release, string packagePath, CellPackageManifest manifest, CancellationToken ct)
    {
        // GitHub Release Asset 上传需要使用特定的 URL 格式
        var uploadUrl = release.Assets.Count > 0
            ? $"https://uploads.github.com/repos/{_config.Owner}/{_config.Repository}/releases/{release.Id}/assets?name={Uri.EscapeDataString(Path.GetFileName(packagePath))}"
            : "";

        if (string.IsNullOrEmpty(uploadUrl))
        {
            _logger.LogWarning("No upload URL available for release");
            return null;
        }

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

    private static string BuildReleaseDescription(CellPackageManifest manifest)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {manifest.Domain} Cell AI");
        sb.AppendLine();
        sb.AppendLine(manifest.Description);
        sb.AppendLine();
        sb.AppendLine("## Metadata");
        sb.AppendLine($"- **Cell ID**: {manifest.CellId}");
        sb.AppendLine($"- **Version**: {manifest.Version}");
        sb.AppendLine($"- **Format**: {manifest.Format}");
        sb.AppendLine($"- **Accuracy**: {manifest.Accuracy:F2}");
        sb.AppendLine($"- **Training Samples**: {manifest.TrainingSamples}");
        sb.AppendLine($"- **Model Size**: {manifest.ModelSizeBytes / 1024.0:F1} KB");
        sb.AppendLine($"- **Compression**: {manifest.Compression}");
        sb.AppendLine($"- **Quantized**: {manifest.IsQuantized}");
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

    private static string ExtractCellIdFromRelease(GitHubRelease release)
    {
        // 从 tag_name 提取: "cellid-v1.0.0" -> "cellid"
        var parts = release.TagName.Split('-');
        return parts.Length > 0 ? parts[0] : release.TagName;
    }

    private static string ExtractDomainFromRelease(GitHubRelease release)
    {
        // 从 name 提取: "code Cell v1.0.0" -> "code"
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
        _logger.LogInformation("GitHubCellRegistry disposed");
    }
}
