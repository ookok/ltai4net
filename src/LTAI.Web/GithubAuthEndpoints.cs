using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LTAI.Core.Governors;
using LTAI.Knowledge.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.Web;

public static class GithubAuthEndpoints
{
    private static readonly Lazy<string> TokenPath = new(() =>
        Path.Combine(OptionService.Get("paths.data") ?? Path.Combine(AppContext.BaseDirectory, "data"), "github_token.json"));

    private static readonly Lazy<string> ClientId = new(() =>
        OptionService.Get("LTAI_GITHUB_CLIENT_ID") ?? "");

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

            var state = Guid.NewGuid().ToString("N");
            context.Response.Cookies.Append("ltai_oauth_state", state, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddMinutes(10)
            });

            var redirectUri = $"https://github.com/login/oauth/authorize?client_id={Uri.EscapeDataString(clientId)}&scope=repo,user&state={Uri.EscapeDataString(state)}";
            context.Response.Redirect(redirectUri);
        });

        endpoints.MapGet("/api/code/github/callback", async (HttpContext context) =>
        {
            var code = context.Request.Query["code"].FirstOrDefault() ?? "";
            var state = context.Request.Query["state"].FirstOrDefault() ?? "";

            if (string.IsNullOrEmpty(code))
            {
                context.Response.StatusCode = 400;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "code is required" }));
                return;
            }

            var expectedState = context.Request.Cookies["ltai_oauth_state"];
            if (string.IsNullOrEmpty(expectedState) || !string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                context.Response.StatusCode = 400;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Invalid state parameter" }));
                return;
            }

            var clientId = ClientId.Value;
            var clientSecret = OptionService.Get("LTAI_GITHUB_CLIENT_SECRET") ?? "";

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

                var tokenResponse = await _http.SendAsync(tokenRequest).ConfigureAwait(false);
                tokenResponse.EnsureSuccessStatusCode();

                var tokenJson = await tokenResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var tokenDoc = JsonDocument.Parse(tokenJson);
                var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString() ?? "";

                if (string.IsNullOrEmpty(accessToken))
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Failed to obtain token" }));
                    return;
                }

                _accessToken = accessToken;

                var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
                userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                userRequest.Headers.UserAgent.ParseAdd("LTAI");

                var userResponse = await _http.SendAsync(userRequest).ConfigureAwait(false);
                userResponse.EnsureSuccessStatusCode();

                var userJson = await userResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
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
                await context.Response.WriteAsync(JsonSerializer.Serialize(new GitHubAuthResponse(true, username, avatarUrl))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message })).ConfigureAwait(false);
            }
        });

        endpoints.MapGet("/api/code/github/status", async (HttpContext context) =>
        {
            var token = _storedToken;
            if (token == null || string.IsNullOrEmpty(token.AccessToken))
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new GitHubAuthResponse(false, null, null))).ConfigureAwait(false);
                return;
            }

            try
            {
                var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
                userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
                userRequest.Headers.UserAgent.ParseAdd("LTAI");
                var userResponse = await _http.SendAsync(userRequest).ConfigureAwait(false);

                if (userResponse.IsSuccessStatusCode)
                {
                    var userJson = await userResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using var userDoc = JsonDocument.Parse(userJson);
                    var username = userDoc.RootElement.GetProperty("login").GetString() ?? "";
                    var avatarUrl = userDoc.RootElement.GetProperty("avatar_url").GetString() ?? "";

                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new GitHubAuthResponse(true, username, avatarUrl))).ConfigureAwait(false);
                }
                else
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new GitHubAuthResponse(false, null, null))).ConfigureAwait(false);
                }
            }
            catch
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new GitHubAuthResponse(false, null, null))).ConfigureAwait(false);
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
                var reposResponse = await _http.SendAsync(reposRequest).ConfigureAwait(false);
                reposResponse.EnsureSuccessStatusCode();

                var reposJson = await reposResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(reposJson).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message })).ConfigureAwait(false);
            }
        });

        endpoints.MapPost("/api/code/github/clone", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync().ConfigureAwait(false);
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
                var repoName = request.RepoFullName.Split('/').Last();
                var codeRoot = OptionService.Get("LTAI_CODE_ROOT") ?? Path.Combine(AppContext.BaseDirectory, "repos");
                Directory.CreateDirectory(codeRoot);

                var targetPath = Path.Combine(codeRoot, repoName);

                if (Directory.Exists(targetPath))
                {
                    Directory.Delete(targetPath, true);
                }

                var authedUrl = token != null && !string.IsNullOrEmpty(token.AccessToken)
                    ? $"https://oauth2:{Uri.EscapeDataString(token.AccessToken)}@github.com/{request.RepoFullName}.git"
                    : $"https://github.com/{request.RepoFullName}.git";

                if (MicroKernel.Default != null)
                {
                    try
                    {
                        var kResult = await MicroKernel.Default.ExecuteAsync(new KernelOp
                        {
                            Command = "git",
                            Arguments = $"clone --branch {branch} --single-branch -- \"{authedUrl}\" \"{targetPath}\"",
                            Timeout = TimeSpan.FromSeconds(60)
                        }, CancellationToken.None).ConfigureAwait(false);

                        if (kResult.Success)
                        {
                            context.Response.ContentType = "application/json";
                            await context.Response.WriteAsync(JsonSerializer.Serialize(new
                            {
                                repo = request.RepoFullName,
                                branch,
                                path = targetPath,
                                cloned = true
                            })).ConfigureAwait(false);
                            return;
                        }
                    }
                    catch { }
                }

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"clone --branch {branch} --single-branch -- \"{authedUrl}\" \"{targetPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi);
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
                    var sanitizedStderr = SanitizeTokenOutput(stderr, token?.AccessToken);
                    var sanitizedStdout = SanitizeTokenOutput(stdout, token?.AccessToken);
                    context.Response.StatusCode = 500;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Clone failed", stderr = sanitizedStderr, stdout = sanitizedStdout }));
                    return;
                }

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    repo = request.RepoFullName,
                    branch,
                    path = targetPath,
                    cloned = true
                })).ConfigureAwait(false);
            }
            catch (Exception)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "GitHub OAuth exchange failed. Please try again." })).ConfigureAwait(false);
            }
        });
    }

    private static string SanitizeTokenOutput(string output, string? token)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(output))
            return output;

        return output
            .Replace(token, "***")
            .Replace(Uri.EscapeDataString(token), "***");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SaveToken()
    {
        try
        {
            var tokenFile = TokenPath.Value;
            var dir = Path.GetDirectoryName(tokenFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_storedToken);
            var bytes = Encoding.UTF8.GetBytes(json);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(tokenFile, encrypted);
        }
        catch { /* non-fatal */ }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void LoadToken()
    {
        try
        {
            var tokenFile = TokenPath.Value;
            if (!File.Exists(tokenFile))
                return;

            var encrypted = File.ReadAllBytes(tokenFile);
            var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(bytes);
            _storedToken = JsonSerializer.Deserialize<StoredGitHubToken>(json);
            if (_storedToken != null)
                _accessToken = _storedToken.AccessToken;
        }
        catch { /* non-fatal */ }
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
