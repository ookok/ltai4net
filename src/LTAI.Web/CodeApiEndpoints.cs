using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LTAI.Web;

public static class CodeApiEndpoints
{
    private static readonly string CodeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "output", "code"));

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", "__pycache__", ".vs", ".idea"
    };

    private static readonly Dictionary<string, string> LanguageMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "C#",
        [".py"] = "Python",
        [".js"] = "JavaScript",
        [".ts"] = "TypeScript",
        [".go"] = "Go",
        [".rs"] = "Rust",
        [".java"] = "Java",
        [".sql"] = "SQL",
        [".html"] = "HTML",
        [".css"] = "CSS",
        [".json"] = "JSON",
        [".xml"] = "XML",
        [".yaml"] = "YAML",
        [".yml"] = "YAML",
        [".md"] = "Markdown",
        [".sh"] = "Shell"
    };

    public static void MapCodeApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        Directory.CreateDirectory(CodeRoot);

        endpoints.MapGet("/api/code/projects", async (HttpContext context) =>
        {
            if (!Directory.Exists(CodeRoot))
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("[]");
                return;
            }

            var dirs = Directory.GetDirectories(CodeRoot)
                .Select(Path.GetFileName)
                .Where(d => d != null)
                .Select(d => d!)
                .OrderBy(d => d)
                .ToList();

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(dirs));
        });

        endpoints.MapPost("/api/code/projects", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var request = JsonSerializer.Deserialize<CreateProjectRequest>(body);

                if (request == null || string.IsNullOrWhiteSpace(request.Name))
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Name is required" }));
                    return;
                }

                var projectDir = Path.Combine(CodeRoot, request.Name);
                if (Directory.Exists(projectDir))
                {
                    context.Response.StatusCode = 409;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Project already exists" }));
                    return;
                }

                Directory.CreateDirectory(projectDir);
                var gitkeep = Path.Combine(projectDir, ".gitkeep");
                await File.WriteAllTextAsync(gitkeep, "");

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { name = request.Name, created = true }));
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        });

        endpoints.MapDelete("/api/code/projects/{name}", async (HttpContext context, string name) =>
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains("..") || name.Contains('/') || name.Contains('\\'))
            {
                context.Response.StatusCode = 400;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Invalid project name" }));
                return;
            }

            var projectDir = Path.Combine(CodeRoot, name);
            if (!Directory.Exists(projectDir))
            {
                context.Response.StatusCode = 404;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Project not found" }));
                return;
            }

            try
            {
                Directory.Delete(projectDir, true);
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { name, deleted = true }));
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        });

        endpoints.MapGet("/api/code/files", async (HttpContext context) =>
        {
            var path = context.Request.Query["path"].FirstOrDefault() ?? "";

            if (path.Contains(".."))
            {
                context.Response.StatusCode = 400;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Invalid path" }));
                return;
            }

            var targetPath = string.IsNullOrEmpty(path)
                ? CodeRoot
                : Path.Combine(CodeRoot, path.TrimStart('/', '\\'));

            if (!Directory.Exists(targetPath))
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("[]");
                return;
            }

            var items = new List<FileItem>();
            CollectFiles(targetPath, targetPath, items);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(items));
        });

        endpoints.MapGet("/api/code/file", async (HttpContext context) =>
        {
            var path = context.Request.Query["path"].FirstOrDefault() ?? "";

            if (string.IsNullOrWhiteSpace(path) || path.Contains(".."))
            {
                context.Response.StatusCode = 400;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Invalid path" }));
                return;
            }

            var filePath = Path.Combine(CodeRoot, path.TrimStart('/', '\\'));

            if (!File.Exists(filePath))
            {
                context.Response.StatusCode = 404;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "File not found" }));
                return;
            }

            var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            var ext = Path.GetExtension(filePath);
            var language = LanguageMap.GetValueOrDefault(ext, "");

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                path = path.TrimStart('/', '\\'),
                content,
                language,
                extension = ext,
                size = new FileInfo(filePath).Length
            }));
        });

        endpoints.MapPut("/api/code/file", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var request = JsonSerializer.Deserialize<FileContentRequest>(body);

                if (request == null || string.IsNullOrWhiteSpace(request.Path) || request.Path.Contains(".."))
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Invalid path" }));
                    return;
                }

                var filePath = Path.Combine(CodeRoot, request.Path.TrimStart('/', '\\'));
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                await File.WriteAllTextAsync(filePath, request.Content ?? "", Encoding.UTF8);

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { path = request.Path, written = true }));
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        });

        endpoints.MapDelete("/api/code/file", async (HttpContext context) =>
        {
            var path = context.Request.Query["path"].FirstOrDefault() ?? "";

            if (string.IsNullOrWhiteSpace(path) || path.Contains(".."))
            {
                context.Response.StatusCode = 400;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Invalid path" }));
                return;
            }

            var filePath = Path.Combine(CodeRoot, path.TrimStart('/', '\\'));

            if (!File.Exists(filePath))
            {
                context.Response.StatusCode = 404;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "File not found" }));
                return;
            }

            File.Delete(filePath);
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { path, deleted = true }));
        });

        endpoints.MapPost("/api/code/diff", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var request = JsonSerializer.Deserialize<DiffRequest>(body);

                if (request == null || string.IsNullOrWhiteSpace(request.Path) || request.Path.Contains(".."))
                {
                    context.Response.StatusCode = 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Invalid path" }));
                    return;
                }

                var targetPath = Path.Combine(CodeRoot, request.Path.TrimStart('/', '\\'));

                if (!Directory.Exists(targetPath))
                {
                    context.Response.StatusCode = 404;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Path not found" }));
                    return;
                }

                var diff = RunGitDiff(targetPath);
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { path = request.Path, diff }));
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        });

        endpoints.MapGet("/api/code/search", async (HttpContext context) =>
        {
            var q = context.Request.Query["q"].FirstOrDefault() ?? "";
            var project = context.Request.Query["project"].FirstOrDefault() ?? "";

            if (string.IsNullOrWhiteSpace(q))
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("[]");
                return;
            }

            var searchRoot = string.IsNullOrEmpty(project)
                ? CodeRoot
                : Path.Combine(CodeRoot, project);

            if (searchRoot.Contains("..") || !Directory.Exists(searchRoot))
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("[]");
                return;
            }

            var results = new List<object>();
            SearchFiles(searchRoot, searchRoot, q, results);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(results));
        });
    }

    private static void CollectFiles(string root, string currentDir, List<FileItem> items)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(currentDir))
            {
                var dirName = Path.GetFileName(dir);
                if (ExcludedDirs.Contains(dirName))
                    continue;

                items.Add(new FileItem(
                    Name: dirName,
                    Path: Path.GetRelativePath(root, dir).Replace('\\', '/'),
                    IsDirectory: true,
                    Size: 0,
                    Extension: null,
                    Language: ""
                ));

                CollectFiles(root, dir, items);
            }

            foreach (var file in Directory.GetFiles(currentDir))
            {
                var fileName = Path.GetFileName(file);
                var ext = Path.GetExtension(file);
                var language = LanguageMap.GetValueOrDefault(ext, "");
                var size = new FileInfo(file).Length;

                items.Add(new FileItem(
                    Name: fileName,
                    Path: Path.GetRelativePath(root, file).Replace('\\', '/'),
                    IsDirectory: false,
                    Size: size,
                    Extension: ext,
                    Language: language
                ));
            }
        }
        catch { /* non-fatal */ }
    }

    private static void SearchFiles(string root, string currentDir, string query, List<object> results)
    {
        try
        {
            foreach (var file in Directory.GetFiles(currentDir, "*.*", SearchOption.AllDirectories))
            {
                var dirName = Path.GetDirectoryName(file);
                if (dirName != null)
                {
                    var parts = dirName.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (parts.Any(p => ExcludedDirs.Contains(p)))
                        continue;
                }

                try
                {
                    var content = File.ReadAllText(file, Encoding.UTF8);
                    var lines = content.Split('\n');
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
                            results.Add(new
                            {
                                file = relativePath,
                                line = i + 1,
                                content = lines[i].TrimEnd('\r')
                            });
                        }
                    }
                }
                catch { /* non-fatal */ }
            }
        }
        catch { /* non-fatal */ }
    }

    private static string RunGitDiff(string path)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "diff -- .",
                WorkingDirectory = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
                return "failed to start git";

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10000);

            return string.IsNullOrWhiteSpace(output) ? "no changes" : output;
        }
        catch (Exception ex)
        {
            return $"git diff error: {ex.Message}";
        }
    }
}

public sealed record FileItem(
    string Name,
    string Path,
    bool IsDirectory,
    long Size,
    string? Extension,
    string Language
);

public sealed record CreateProjectRequest
{
    public string Name { get; init; } = string.Empty;
}

public sealed record FileContentRequest
{
    public string Path { get; init; } = string.Empty;
    public string? Content { get; init; }
}

public sealed record DiffRequest
{
    public string Path { get; init; } = string.Empty;
}
