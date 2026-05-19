using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.Web;

public static class GithubAuthEndpoints
{
    private static readonly Lazy<string> TokenPath = new(() =>
        Path.Combine(AppContext.BaseDirectory, "data", "github_token.json"));

    private static readonly Lazy<string> ClientId = new(() =>
        Environment.GetEnvironmentVariable("LTAI_GITHUB_CLIENT_ID") ?? "");

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static string? _accessToken;
    private static StoredGitHubToken? _storedToken;

    private sealed class StoredGitHubToken
    {
        public string AccessToken { get; init; } = string.Empty;
        public string Username { get; init; } = string.Empty;
        public string AvatarUrl { get; init; } = string.Empty;
        public DateTime StoredAt { get; init; }
    }

    static GithubAuthEndpoints()
    {
        LoadToken();
    }

    public static void MapGithubAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/code/github/auth", async (HttpContext context) =>
        {
            var clientId = ClientId.Value;
            if (string.IsNullOrEmpty(clientId))
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "LTAI_GITHUB_CLIENT_ID not configured" }));
                return;
            }

            var redirectUri = $"https://github.com/login/oauth/authorize?client_id={Uri.EscapeDataString(clientId)}&scope=repo,user";
            context.Response.Redirect(redirectUri);
        });

        endpoints.MapGet("/api/code/github/callback", async (HttpContext context) =>
        {
            var code = context.Request.Query["code"].FirstOrDefault() ?? "";

            if (string.IsNullOrEmpty(code))
            {
                context.Response.StatusCode = 400;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "code is required" }));
                return;
            }

            var clientId = ClientId.Value;
            var clientSecret = Environment.GetEnvironmentVariable("LTAI_GITHUB_CLIENT_SECRET") ?? "";

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "GitHub OAuth not configured" }));
                return;
            }

            try
            {
                var tokenPayload = JsonSerializer.Serialize(new
                {
                    client_id = clientId,
                    client_secret = clientSecret,
                    code
                });

                var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
                {
                    Content = new StringContent(tokenPayload, Encoding.UTF8, "application/json")
                };
                tokenRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var tokenResponse = await _http.SendAsync(tokenRequest);
                tokenResponse.EnsureSuccessStatusCode();

                var tokenJson = await tokenResponse.Content.ReadAsStringAsync();
                using var tokenDoc = JsonDocument.Parse(tokenJson);
                var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString() ?? "";

                if (string.IsNullOrEmpty(accessToken))
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Failed to obtain token", raw = tokenJson }));
                    return;
                }

                _accessToken = accessToken;

                var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
                userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                userRequest.Headers.UserAgent.ParseAdd("LTAI");

                var userResponse = await _http.SendAsync(userRequest);
                userResponse.EnsureSuccessStatusCode();

                var userJson = await userResponse.Content.ReadAsStringAsync();
                using var userDoc = JsonDocument.Parse(userJson);
                var username = userDoc.RootElement.GetProperty("login").GetString() ?? "";
                var avatarUrl = userDoc.RootElement.GetProperty("avatar_url").GetString() ?? "";

                _storedToken = new StoredGitHubToken
                {
                    AccessToken = accessToken,
                    Username = username,
                    AvatarUrl = avatarUrl,
                    StoredAt = DateTime.UtcNow
                };
                SaveToken();

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new GitHubAuthResponse(true, username, avatarUrl)));
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        });

        endpoints.MapGet("/api/code/github/status", async (HttpContext context) =>
        {
            var token = _storedToken;
            if (token == null || string.IsNullOrEmpty(token.AccessToken))
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new GitHubAuthResponse(false, null, null)));
                return;
            }

            try
            {
                var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
                userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
                userRequest.Headers.UserAgent.ParseAdd("LTAI");
                var userResponse = await _http.SendAsync(userRequest);

                if (userResponse.IsSuccessStatusCode)
                {
                    var userJson = await userResponse.Content.ReadAsStringAsync();
                    using var userDoc = JsonDocument.Parse(userJson);
                    var username = userDoc.RootElement.GetProperty("login").GetString() ?? "";
                    var avatarUrl = userDoc.RootElement.GetProperty("avatar_url").GetString() ?? "";

                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new GitHubAuthResponse(true, username, avatarUrl)));
                }
                else
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new GitHubAuthResponse(false, null, null)));
                }
            }
            catch
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new GitHubAuthResponse(false, null, null)));
            }
        });

        endpoints.MapGet("/api/code/github/repos", async (HttpContext context) =>
        {
            var token = _storedToken;
            if (token == null || string.IsNullOrEmpty(token.AccessToken))
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Not authenticated" }));
                return;
            }

            try
            {
                var reposRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/repos?per_page=100&sort=updated");
                reposRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
                reposRequest.Headers.UserAgent.ParseAdd("LTAI");
                var reposResponse = await _http.SendAsync(reposRequest);
                reposResponse.EnsureSuccessStatusCode();

                var reposJson = await reposResponse.Content.ReadAsStringAsync();
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(reposJson);
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        });

        endpoints.MapPost("/api/code/github/clone", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var request = JsonSerializer.Deserialize<CloneRepoRequest>(body);

                if (request == null || string.IsNullOrWhiteSpace(request.RepoFullName))
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "RepoFullName is required" }));
                    return;
                }

                var token = _storedToken;
                var branch = string.IsNullOrWhiteSpace(request.Branch) ? "main" : request.Branch;
                var cloneUrl = token != null && !string.IsNullOrEmpty(token.AccessToken)
                    ? $"https://oauth2:{token.AccessToken}@github.com/{request.RepoFullName}.git"
                    : $"https://github.com/{request.RepoFullName}.git";

                var codeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "output", "code"));
                Directory.CreateDirectory(codeRoot);

                var repoName = request.RepoFullName.Split('/').LastOrDefault() ?? request.RepoFullName;
                var targetPath = Path.Combine(codeRoot, repoName);

                if (Directory.Exists(targetPath))
                {
                    Directory.Delete(targetPath, true);
                }

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"clone --branch {branch} --single-branch {cloneUrl} \"{targetPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(startInfo);
                if (process == null)
                {
                    context.Response.StatusCode = 500;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Failed to start git clone" }));
                    return;
                }

                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(60000);

                if (process.ExitCode != 0)
                {
                    context.Response.StatusCode = 500;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Clone failed", stderr, stdout }));
                    return;
                }

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    repo = request.RepoFullName,
                    branch,
                    path = targetPath,
                    cloned = true
                }));
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        });
    }

    private static void LoadToken()
    {
        try
        {
            var tokenFile = TokenPath.Value;
            if (!File.Exists(tokenFile))
                return;

            var json = File.ReadAllText(tokenFile, Encoding.UTF8);
            _storedToken = JsonSerializer.Deserialize<StoredGitHubToken>(json);
            if (_storedToken != null)
                _accessToken = _storedToken.AccessToken;
        }
        catch
        {
        }
    }

    private static void SaveToken()
    {
        try
        {
            var tokenFile = TokenPath.Value;
            var dir = Path.GetDirectoryName(tokenFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_storedToken);
            File.WriteAllText(tokenFile, json, Encoding.UTF8);
        }
        catch
        {
        }
    }
}

public sealed record GitHubAuthResponse(
    bool Authenticated,
    string? Username,
    string? AvatarUrl
);

public sealed record CloneRepoRequest
{
    public string RepoFullName { get; init; } = string.Empty;
    public string? Branch { get; init; } = "main";
}
