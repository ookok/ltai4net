using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using LTAI.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Tools;

[ToolDomain("build")]
public sealed class BuildTools
{
    private readonly string _ws;

    public BuildTools(string ws) => _ws = ws;

    [Description("Auto-detect project type and build. Returns structured result with parsed errors.\n"
        + "Detects: Cargo.toml → cargo build, package.json → npm run build,\n"
        + "go.mod → go build, Makefile → make, pom.xml → mvn compile,\n"
        + "build.gradle → gradle build, CMakeLists.txt → cmake --build,\n"
        + "pyproject.toml → pip, *.sln/*.csproj → dotnet build")]
    public async Task<string> BuildProject(
        [Description("Optional subdirectory or build file path (relative to workspace)")] string? target = null)
    {
        var (cmd, buildDir) = DetectBuildCommand(target);
        if (string.IsNullOrEmpty(cmd))
            return "[Build] ❌ 无法检测项目类型。支持: cargo/package.json/go.mod/Makefile/maven/gradle/cmake/pyproject/dotnet";

        var result = await RunBuildCommand(cmd, buildDir).ConfigureAwait(false);
        return result;
    }

    [Description("Build and auto-fix errors (max 3 rounds). Detects project type automatically.")]
    public async Task<string> BuildAndFix(
        [Description("Optional subdirectory or build file path")] string? target = null)
    {
        for (var round = 1; round <= 3; round++)
        {
            var result = await BuildProject(target).ConfigureAwait(false);
            if (!result.Contains("❌")) return result;

            // Extract first few errors and suggest fixes
            var errors = ExtractErrors(result);
            if (errors.Count == 0) return result;

            var suggestions = errors.Select(GenericSuggestFix).Where(s => s != null).Take(3);
            var tip = string.Join("\n", suggestions);
            return $"{result}\n\n--- 自动修复提示 ---\n{tip}\n(第 {round}/3 轮，将在修复后重新构建)";
        }
        return await BuildProject(target).ConfigureAwait(false);
    }

    [Description("Detect project type and show the appropriate build command without running it.")]
    public static string DetectBuild(
        [Description("Workspace path to scan")] string? workspacePath = null)
    {
        var dir = workspacePath ?? Environment.CurrentDirectory;
        var (cmd, _) = new BuildTools(dir).DetectBuildCommand(null);
        return string.IsNullOrEmpty(cmd)
            ? $"[Build] ⚠️ {dir}: 未检测到已知构建文件"
            : $"[Build] ℹ️ {dir}: 使用 `{cmd}`";
    }

    [Description("Parse arbitrary build output and extract structured error information.")]
    public static string ParseBuildOutput(
        [Description("Raw build output text")] string output)
    {
        var errors = ExtractErrors(output);
        if (errors.Count == 0 && !output.Contains("error", StringComparison.OrdinalIgnoreCase))
            return $"[Build] ✅ 构建成功\n{output.Trim()}";

        var sb = new System.Text.StringBuilder();
        var isFailure = output.Contains("FAILED", StringComparison.OrdinalIgnoreCase)
            || output.Contains("error:", StringComparison.OrdinalIgnoreCase)
            || errors.Count > 0;
        sb.AppendLine(isFailure
            ? $"[Build] ❌ 构建失败 ({errors.Count} errors)"
            : $"[Build] ✅ 构建成功");
        sb.AppendLine();
        foreach (var e in errors.Take(10))
            sb.AppendLine($"  {e.file}:{e.line}  {e.code}: {e.message}");
        if (errors.Count > 10)
            sb.AppendLine($"  ... and {errors.Count - 10} more errors");
        sb.AppendLine();
        sb.AppendLine("--- 输出 ---");
        sb.Append(output.Trim());
        return sb.ToString();
    }

    // ── internal ──

    private (string cmd, string buildDir) DetectBuildCommand(string? target)
    {
        var searchDir = string.IsNullOrEmpty(target) ? _ws
            : Path.IsPathRooted(target) ? target
            : Path.Combine(_ws, target);

        if (!string.IsNullOrEmpty(target) && target.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            return ($"dotnet build \"{target}\" --nologo -v q 2>&1", _ws);
        if (!string.IsNullOrEmpty(target) && (target.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || target.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
            || target.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)))
            return ($"dotnet build \"{target}\" --nologo -v q 2>&1", _ws);

        // Scan for build files (most specific first)
        if (File.Exists(Path.Combine(searchDir, "Cargo.toml")))
            return ("cargo build 2>&1", searchDir);
        if (File.Exists(Path.Combine(searchDir, "package.json")))
        {
            // Prefer lock file, then build script
            return File.Exists(Path.Combine(searchDir, "bun.lock"))
                ? ("bun run build 2>&1", searchDir)
                : File.Exists(Path.Combine(searchDir, "pnpm-lock.yaml"))
                ? ("pnpm run build 2>&1", searchDir)
                : File.Exists(Path.Combine(searchDir, "yarn.lock"))
                ? ("yarn build 2>&1", searchDir)
                : ("npm run build 2>&1", searchDir);
        }
        if (File.Exists(Path.Combine(searchDir, "go.mod")))
            return ("go build ./... 2>&1", searchDir);
        if (File.Exists(Path.Combine(searchDir, "Makefile")) || File.Exists(Path.Combine(searchDir, "makefile")))
            return ("make 2>&1", searchDir);
        if (File.Exists(Path.Combine(searchDir, "pom.xml")))
            return ("mvn compile 2>&1", searchDir);
        if (File.Exists(Path.Combine(searchDir, "build.gradle")) || File.Exists(Path.Combine(searchDir, "build.gradle.kts")))
            return ("gradle build 2>&1", searchDir);
        if (File.Exists(Path.Combine(searchDir, "CMakeLists.txt")))
            return ("cmake --build . 2>&1", searchDir);
        if (File.Exists(Path.Combine(searchDir, "pyproject.toml")))
            return ("pip install -e . 2>&1", searchDir);
        if (File.Exists(Path.Combine(searchDir, "stack.yaml")) || File.Exists(Path.Combine(searchDir, "package.yaml")))
            return ("stack build 2>&1", searchDir);
        if (File.Exists(Path.Combine(searchDir, "mix.exs")))
            return ("mix compile 2>&1", searchDir);
        if (File.Exists(Path.Combine(searchDir, "dub.sdl")) || File.Exists(Path.Combine(searchDir, "dub.json")))
            return ("dub build 2>&1", searchDir);
        if (File.Exists(Path.Combine(searchDir, "Bundle.toml")) || File.Exists(Path.Combine(searchDir, "wally.toml")))
            return ("rojo build 2>&1", searchDir);
        if (File.Exists(Path.Combine(searchDir, "shard.yml")))
            return ("crystal build 2>&1", searchDir);
        if (File.Exists(Path.Combine(searchDir, "pubspec.yaml")))
            return ("dart compile 2>&1", searchDir);

        // Fallback: scan for .sln in workspace root
        var slnFiles = Directory.GetFiles(_ws, "*.sln", SearchOption.TopDirectoryOnly);
        if (slnFiles.Length > 0)
            return ($"dotnet build \"{slnFiles[0]}\" --nologo -v q 2>&1", _ws);

        var csprojFiles = Directory.GetFiles(_ws, "*.csproj", SearchOption.TopDirectoryOnly);
        if (csprojFiles.Length > 0)
            return ($"dotnet build \"{csprojFiles[0]}\" --nologo -v q 2>&1", _ws);

        return ("", "");
    }

    private async Task<string> RunBuildCommand(string cmd, string buildDir)
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
        proc.Start();
        var output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var error = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
        proc.WaitForExit(TimeSpan.FromSeconds(300));

        var full = output + error;
        return FormatBuildResult(proc.ExitCode, full);
    }

    private static string FormatBuildResult(int exitCode, string output)
    {
        var errors = ExtractErrors(output);
        if (exitCode == 0 && errors.Count == 0)
            return $"[Build] ✅ 构建成功\n{output.Trim()}";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[Build] ❌ 构建失败 (exit={exitCode}, {errors.Count} errors)");
        sb.AppendLine();
        foreach (var e in errors.Take(10))
            sb.AppendLine($"  {e.file}:{e.line}  {e.code}: {e.message}");
        if (errors.Count > 10)
            sb.AppendLine($"  ... and {errors.Count - 10} more errors");
        sb.AppendLine();
        sb.AppendLine("--- 输出 ---");
        sb.Append(output.Trim());
        return sb.ToString();
    }

    private static readonly Regex[] ErrorPatterns =
    [
        // GCC/Clang/MSBuild: file:line:col: error/warning: message
        new(@"^(?<file>.+?):(?<line>\d+):(?<col>\d+):\s*(?:fatal\s+)?(?<level>error|warning)\s*(?<code>\w+)?\s*:\s*(?<message>.+)$", RegexOptions.Multiline | RegexOptions.Compiled),
        // MSBuild: file(line,col): error/warning CODE: message
        new(@"^(?<file>.+?)\((?<line>\d+)(?:,(?<col>\d+))?\)\s*:\s*(?<level>error|warning)\s*(?<code>\w+)\s*:\s*(?<message>.+)$", RegexOptions.Multiline | RegexOptions.Compiled),
        // Rust: error[E0000]: message --> file:line:col
        new(@"^error\[(?<code>E\d+)\]:\s*(?<message>.+)$", RegexOptions.Multiline | RegexOptions.Compiled),
        // TypeScript: file.ext(line,col): error TS0000: message
        new(@"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<level>error|warning)\s*(?<code>TS\d+)?\s*:\s*(?<message>.+)$", RegexOptions.Multiline | RegexOptions.Compiled),
        // Generic: ERROR: message at file:line
        new(@"(?<level>ERROR|Error|error)\s*:?\s*(?<message>.+?)\s+at\s+(?<file>.+?):(?<line>\d+)", RegexOptions.Multiline | RegexOptions.Compiled),
    ];

    internal static List<(string file, int line, string code, string message)> ExtractErrors(string output)
    {
        var seen = new HashSet<string>();
        var errors = new List<(string file, int line, string code, string message)>();

        foreach (var regex in ErrorPatterns)
        {
            foreach (Match m in regex.Matches(output))
            {
                var file = m.Groups["file"].Value;
                var line = int.TryParse(m.Groups["line"].Value, out var l) ? l : 0;
                var code = m.Groups["code"].Value;
                var message = m.Groups["message"].Value.Trim();
                var key = $"{file}:{line}:{code}:{message}";
                if (!string.IsNullOrEmpty(message) && seen.Add(key))
                    errors.Add((file, line, code, message));
            }
        }

        // Parse Rust error continuation lines (--> file:line)
        if (errors.Count > 0)
        {
            var rustLines = Regex.Matches(output, @"-->\s+(?<file>.+?):(?<line>\d+):(?<col>\d+)");
            foreach (Match m in rustLines)
            {
                var file = m.Groups["file"].Value.Trim();
                var line = int.TryParse(m.Groups["line"].Value, out var l) ? l : 0;
                // Find a recent Rust error without file info and attach this location
                for (int i = errors.Count - 1; i >= 0 && i > errors.Count - 5; i--)
                {
                    var e = errors[i];
                    if (string.IsNullOrEmpty(e.file) && e.code.StartsWith("E", StringComparison.Ordinal))
                    {
                        errors[i] = (file, line, e.code, e.message);
                        break;
                    }
                }
            }
        }

        return errors;
    }

    private static string? GenericSuggestFix((string file, int line, string code, string message) error)
    {
        var code_lower = error.code.ToLowerInvariant();
        var msg_lower = error.message.ToLowerInvariant();

        // C# / .NET
        if (code_lower.Contains("cs0246") || code_lower.Contains("cs0103"))
            return $"📝 `{error.file}:{error.line}` — 可能缺少 using/import 语句或类型名称错误";
        if (code_lower.Contains("cs0117") || code_lower.Contains("cs1061"))
            return $"📝 `{error.file}:{error.line}` — 成员不存在，检查拼写或类型是否正确";
        if (code_lower.Contains("cs1503"))
            return $"📝 `{error.file}:{error.line}` — 参数类型不匹配，检查函数签名";

        // Rust
        if (code_lower.StartsWith("e") && char.IsDigit(code_lower.Length > 1 ? code_lower[1] : '0'))
            return $"📝 `{error.file}:{error.line}` — Rust 编译错误 `{error.code}`，检查类型和生命周期标注";
        if (msg_lower.Contains("expected") && msg_lower.Contains("found"))
            return $"📝 `{error.file}:{error.line}` — 类型不匹配，检查期待类型和实际类型";

        // TypeScript
        if (code_lower.StartsWith("ts"))
            return $"📝 `{error.file}:{error.line}` — TypeScript `{error.code}`";
        if (msg_lower.Contains("cannot find name"))
            return $"📝 `{error.file}:{error.line}` — 未定义标识符，检查 import 或变量声明";
        if (msg_lower.Contains("type") && msg_lower.Contains("not assignable"))
            return $"📝 `{error.file}:{error.line}` — 类型不兼容";

        // Generic
        if (msg_lower.Contains("undefined") || msg_lower.Contains("not found"))
            return $"📝 `{error.file}:{error.line}` — 未定义的引用，检查拼写或导入";
        if (msg_lower.Contains("syntax") || msg_lower.Contains("unexpected"))
            return $"📝 `{error.file}:{error.line}` — 语法错误，检查括号/分号/缩进";
        if (msg_lower.Contains("cannot find module") || msg_lower.Contains("module not found"))
            return $"📝 `{error.file}:{error.line}` — 模块未找到，检查路径或包名";
        if (msg_lower.Contains("cannot find symbol") || msg_lower.Contains("does not exist"))
            return $"📝 `{error.file}:{error.line}` — 符号未找到，检查类名/方法名";

        return null;
    }
}
