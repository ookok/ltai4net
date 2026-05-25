using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LTAI.Tools.Integration;

public sealed class SelfUpdater : IDisposable
{
    private static readonly Lazy<SelfUpdater> _instance = new(() => new SelfUpdater());
    public static SelfUpdater Instance => _instance.Value;

    private readonly HttpClient _http;
    private readonly string _projectRoot;
    private const string GitHubReleases = "https://api.github.com/repos/ookok/ltai4net/releases/latest";
    private const string GiteeReleases = "https://gitee.com/api/v5/repos/ookok/ltai4net/releases/latest";

    private SelfUpdater()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LTAI/3.0");
        _projectRoot = AppDomain.CurrentDomain.BaseDirectory;
    }

    public void Dispose() { _http?.Dispose(); }

    public async Task<Dictionary<string, object>> CheckUpdateAsync(bool useMirror = false)
    {
        var url = useMirror ? GiteeReleases : GitHubReleases;

        try
        {
            var response = await _http.GetStringAsync(url).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<JsonElement>(response);

            var version = data.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
            var name = data.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var publishedAt = data.TryGetProperty("published_at", out var p) ? p.GetString() ?? "" : "";
            var htmlUrl = data.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

            return new Dictionary<string, object>
            {
                ["version"] = version,
                ["name"] = name,
                ["published_at"] = publishedAt,
                ["url"] = htmlUrl
            };
        }
        catch (HttpRequestException) when (!useMirror)
        {
            return await CheckUpdateAsync(useMirror: true).ConfigureAwait(false);
        }
        catch
        {
            return new Dictionary<string, object> { ["error"] = "Could not check for updates" };
        }
    }

    public async Task<Dictionary<string, object>> GitPullAsync()
    {
        var repoPath = Directory.GetParent(_projectRoot)?.Parent?.Parent?.Parent?.FullName ?? _projectRoot;

        if (!Directory.Exists(Path.Combine(repoPath, ".git")))
            return new Dictionary<string, object> { ["error"] = "Not a git repository" };

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "pull --ff-only origin master",
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
                return new Dictionary<string, object> { ["error"] = "Could not start git process" };

            var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            if (process.ExitCode != 0)
                return new Dictionary<string, object> { ["error"] = error[..Math.Min(300, error.Length)] };

            if (output.Contains("Already up to date"))
                return new Dictionary<string, object> { ["status"] = "up_to_date" };

            return new Dictionary<string, object>
            {
                ["status"] = "updated",
                ["method"] = "git",
                ["message"] = output[..Math.Min(500, output.Length)],
                ["restart_required"] = true
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { ["error"] = ex.Message };
        }
    }

    public async Task<Dictionary<string, object>> RunUpdateAsync(
        bool dryRun = false, bool checkOnly = false, bool useMirror = false)
    {
        var gitResult = await GitPullAsync().ConfigureAwait(false);
        if (gitResult.ContainsKey("status") && gitResult["status"]?.ToString() == "updated")
            return gitResult;
        if (gitResult.TryGetValue("status", out var s) && s?.ToString() == "up_to_date")
            return gitResult;

        var info = await CheckUpdateAsync(useMirror).ConfigureAwait(false);
        if (info.ContainsKey("error"))
            return new Dictionary<string, object> { ["status"] = "no_update", ["message"] = info["error"] };

        if (checkOnly)
        {
            info["status"] = "update_available";
            return info;
        }

        if (dryRun)
        {
            info["status"] = "would_update";
            return info;
        }

        return new Dictionary<string, object>
        {
            ["status"] = "check_complete",
            ["message"] = "Use git pull for updates",
            ["version"] = info.GetValueOrDefault("version", "")
        };
    }
}
