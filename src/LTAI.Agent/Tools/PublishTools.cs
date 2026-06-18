using System.ComponentModel;
using System.Diagnostics;
using LTAI.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Tools;

[ToolDomain("build")]
public sealed class PublishTools
{
    private readonly string _ws;

    public PublishTools(string ws) => _ws = ws;

    [Description("Publish/build for production: detects project type and runs appropriate publish command.\n"
        + "dotnet → dotnet publish, npm → npm run build && npm pack,\n"
        + "cargo → cargo build --release, go → go build -ldflags=\"-s -w\",\n"
        + "maven → mvn package, gradle → gradle build")]
    public async Task<string> PublishProject(
        [Description("Optional subdirectory or build file")] string? target = null,
        [Description("Output directory (default: dist/)")] string? outputDir = null)
    {
        var (cmd, buildDir) = DetectPublishCommand(target, outputDir);
        if (string.IsNullOrEmpty(cmd))
            return "[Publish] ❌ 无法检测项目发布方式。支持: dotnet/npm/cargo/go/maven/gradle";

        return await RunPublishCommand(cmd, buildDir).ConfigureAwait(false);
    }

    [Description("Detect project type and show the suitable publish command without running it.")]
    public static string DetectPublish(
        [Description("Workspace path to scan")] string? workspacePath = null)
    {
        var dir = workspacePath ?? Environment.CurrentDirectory;
        var (cmd, _) = new PublishTools(dir).DetectPublishCommand(null, null);
        return string.IsNullOrEmpty(cmd)
            ? $"[Publish] ⚠️ {dir}: 未检测到已知项目类型"
            : $"[Publish] ℹ️ {dir}: 可使用 `{cmd}`";
    }

    [Description("List all published/distributed artifacts in dist/ or output directory.")]
    public static string ListPublished(
        [Description("Directory to scan (default: dist/ under workspace)")] string? dir = null)
    {
        var scanDir = dir ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "dist");
        if (!Directory.Exists(scanDir))
            scanDir = Path.Combine(Environment.CurrentDirectory, "dist");

        if (!Directory.Exists(scanDir))
            return "[Publish] 未找到发布目录。请先运行发布或指定目录。";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[Publish] 发布产物:");
        long totalSize = 0;
        foreach (var sub in Directory.GetDirectories(scanDir))
        {
            var name = Path.GetFileName(sub);
            var files = Directory.GetFiles(sub, "*", SearchOption.AllDirectories);
            var size = files.Sum(f => new FileInfo(f).Length);
            totalSize += size;
            var exeCount = files.Count(f =>
                f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase));
            sb.AppendLine($"  {name,-16} {exeCount,4} exes, {files.Length,5} files, {size / 1024 / 1024,4} MB");
        }
        var allFiles = Directory.GetFiles(scanDir, "*", SearchOption.AllDirectories);
        if (allFiles.Length > 0)
        {
            var topLevel = allFiles.Where(f => Path.GetDirectoryName(f) == scanDir).ToList();
            if (topLevel.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"  根目录文件: {topLevel.Count}");
            }
        }
        sb.AppendLine();
        sb.AppendLine($"  总计: {allFiles.Length} files, {totalSize / 1024 / 1024} MB");
        return sb.ToString();
    }

    // ── internal ──

    private (string cmd, string buildDir) DetectPublishCommand(string? target, string? outputDir)
    {
        var searchDir = string.IsNullOrEmpty(target) ? _ws
            : Path.IsPathRooted(target) ? target
            : Path.Combine(_ws, target);

        var outFlag = string.IsNullOrEmpty(outputDir) ? "" : outputDir;

        // Check project files
        if (File.Exists(Path.Combine(searchDir, "Cargo.toml")))
            return ("cargo build --release 2>&1", searchDir);
        if (File.Exists(Path.Combine(searchDir, "package.json")))
        {
            return File.Exists(Path.Combine(searchDir, "bun.lock"))
                ? ("bun run build && bun run publish 2>&1", searchDir)
                : File.Exists(Path.Combine(searchDir, "pnpm-lock.yaml"))
                ? ("pnpm run build --production 2>&1", searchDir)
                : ("npm run build 2>&1", searchDir);
        }
        if (File.Exists(Path.Combine(searchDir, "go.mod")))
            return ("go build -ldflags=\"-s -w\" -o dist/ 2>&1", searchDir);
        if (File.Exists(Path.Combine(searchDir, "pom.xml")))
            return ("mvn package -DskipTests 2>&1", searchDir);
        if (File.Exists(Path.Combine(searchDir, "build.gradle")) || File.Exists(Path.Combine(searchDir, "build.gradle.kts")))
            return ("gradle build -x test 2>&1", searchDir);
        if (File.Exists(Path.Combine(searchDir, "Makefile")))
            return ("make release 2>&1", searchDir);
        if (File.Exists(Path.Combine(searchDir, "pyproject.toml")))
            return ("pip install build && python -m build 2>&1", searchDir);
        if (File.Exists(Path.Combine(searchDir, "CMakeLists.txt")))
            return ("cmake --build . --config Release 2>&1", searchDir);

        // Dotnet: detect .sln or .csproj
        var slnFiles = Directory.GetFiles(searchDir, "*.sln", SearchOption.TopDirectoryOnly);
        if (slnFiles.Length > 0)
            return ($"dotnet publish \"{slnFiles[0]}\" -c Release --nologo -v q 2>&1", searchDir);
        var csprojFiles = Directory.GetFiles(searchDir, "*.csproj", SearchOption.TopDirectoryOnly);
        if (csprojFiles.Length > 0)
        {
            var outDir = string.IsNullOrEmpty(outFlag) ? "" : $" -o \"{outFlag}\"";
            return ($"dotnet publish \"{csprojFiles[0]}\" -c Release{outDir} --nologo -v q 2>&1", searchDir);
        }

        return ("", "");
    }

    private async Task<string> RunPublishCommand(string cmd, string buildDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
            Arguments = OperatingSystem.IsWindows() ? $"/c \"{cmd}\"" : $"-c \"{cmd}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = buildDir,
        };
        using var proc = new Process { StartInfo = psi };
        var sw = Stopwatch.StartNew();
        proc.Start();
        var output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var error = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
        proc.WaitForExit(TimeSpan.FromSeconds(600));

        var success = proc.ExitCode == 0;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(success
            ? $"[Publish] ✅ 发布成功 (耗时 {sw.Elapsed.TotalSeconds:F1}s)"
            : $"[Publish] ❌ 发布失败 (exit={proc.ExitCode}, 耗时 {sw.Elapsed.TotalSeconds:F1}s)");

        if (!success)
        {
            var errText = (output + error).Trim();
            if (errText.Length > 0)
                sb.AppendLine($"  错误: {errText[..Math.Min(errText.Length, 500)]}");
        }
        sb.AppendLine();
        sb.AppendLine("--- 输出 ---");
        sb.Append((output + error).Trim());
        return sb.ToString();
    }
}
