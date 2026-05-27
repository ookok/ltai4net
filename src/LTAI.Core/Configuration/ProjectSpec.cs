namespace LTAI.Core.Configuration;

public sealed record ProjectSpec
{
    public string Language { get; init; } = "";
    public string BuildCommand { get; init; } = "";
    public string BuildArgs { get; init; } = "";
    public string TestCommand { get; init; } = "";
    public string TestArgs { get; init; } = "";
    public string LintCommand { get; init; } = "";
    public string LintArgs { get; init; } = "";
    public string FormatCommand { get; init; } = "";
    public string FormatArgs { get; init; } = "";
    public string RunCommand { get; init; } = "";
    public string RunArgs { get; init; } = "";
    public string PackageManager { get; init; } = "";
    public string PatchManagerInstall { get; init; } = "";
    public string PatchManagerLockFile { get; init; } = "";
    public string[] ProjectFilePatterns { get; init; } = Array.Empty<string>();
    public string[] SourceExtensions { get; init; } = Array.Empty<string>();
    public string TestDirPattern { get; init; } = "tests";
    public string BuildDirPattern { get; init; } = "src";
    public Dictionary<string, string> EnvVars { get; init; } = new();
    public int DetectionScore { get; set; }
    public string PresetName { get; set; } = "";
}

public static class ToolchainPresets
{
    public static ProjectSpec Dotnet => new()
    {
        Language = "dotnet",
        BuildCommand = "dotnet",
        BuildArgs = "build --no-restore",
        TestCommand = "dotnet",
        TestArgs = "test --no-build --nologo",
        LintCommand = "dotnet",
        LintArgs = "build --no-restore --warnaserror",
        FormatCommand = "dotnet",
        FormatArgs = "format --verify-no-changes --verbosity quiet",
        RunCommand = "dotnet",
        RunArgs = "run --no-build --project {project}",
        PackageManager = "dotnet",
        PatchManagerInstall = "add package {package}",
        PatchManagerLockFile = "*.csproj",
        ProjectFilePatterns = new[] { "*.csproj", "*.sln", "*.fsproj" },
        SourceExtensions = new[] { ".cs", ".fs", ".vb", ".cshtml", ".razor" },
        TestDirPattern = "tests",
        BuildDirPattern = "src",
        EnvVars = new() { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" }
    };

    public static ProjectSpec Node => new()
    {
        Language = "node",
        BuildCommand = "npm",
        BuildArgs = "run build",
        TestCommand = "npm",
        TestArgs = "test -- --passWithNoTests",
        LintCommand = "npm",
        LintArgs = "run lint",
        FormatCommand = "npm",
        FormatArgs = "run format -- --check",
        RunCommand = "npm",
        RunArgs = "start",
        PackageManager = "npm",
        PatchManagerInstall = "install {package}",
        PatchManagerLockFile = "package-lock.json",
        ProjectFilePatterns = new[] { "package.json", "yarn.lock", "pnpm-lock.yaml" },
        SourceExtensions = new[] { ".js", ".ts", ".jsx", ".tsx", ".mjs", ".cjs" },
        TestDirPattern = "test",
        BuildDirPattern = "src"
    };

    public static ProjectSpec Python => new()
    {
        Language = "python",
        BuildCommand = "",
        BuildArgs = "",
        TestCommand = "python",
        TestArgs = "-m pytest --tb=short",
        LintCommand = "python",
        LintArgs = "-m ruff check .",
        FormatCommand = "python",
        FormatArgs = "-m ruff format --diff .",
        RunCommand = "python",
        RunArgs = "{project}",
        PackageManager = "pip",
        PatchManagerInstall = "install {package}",
        PatchManagerLockFile = "requirements.txt",
        ProjectFilePatterns = new[] { "pyproject.toml", "setup.py", "requirements.txt", "Pipfile" },
        SourceExtensions = new[] { ".py", ".pyi", ".pyx" },
        TestDirPattern = "tests",
        BuildDirPattern = "src"
    };

    public static ProjectSpec Go => new()
    {
        Language = "go",
        BuildCommand = "go",
        BuildArgs = "build ./...",
        TestCommand = "go",
        TestArgs = "test ./... -count=1",
        LintCommand = "golangci-lint",
        LintArgs = "run ./...",
        FormatCommand = "gofmt",
        FormatArgs = "-d .",
        RunCommand = "go",
        RunArgs = "run {project}",
        PackageManager = "go",
        PatchManagerInstall = "get {package}",
        PatchManagerLockFile = "go.sum",
        ProjectFilePatterns = new[] { "go.mod", "go.sum" },
        SourceExtensions = new[] { ".go" },
        TestDirPattern = "",
        BuildDirPattern = "cmd"
    };

    public static ProjectSpec Rust => new()
    {
        Language = "rust",
        BuildCommand = "cargo",
        BuildArgs = "build",
        TestCommand = "cargo",
        TestArgs = "test",
        LintCommand = "cargo",
        LintArgs = "clippy -- -D warnings",
        FormatCommand = "cargo",
        FormatArgs = "fmt -- --check",
        RunCommand = "cargo",
        RunArgs = "run",
        PackageManager = "cargo",
        PatchManagerInstall = "add {package}",
        PatchManagerLockFile = "Cargo.lock",
        ProjectFilePatterns = new[] { "Cargo.toml", "Cargo.lock" },
        SourceExtensions = new[] { ".rs" },
        TestDirPattern = "tests",
        BuildDirPattern = "src"
    };

    public static ProjectSpec Java => new()
    {
        Language = "java",
        BuildCommand = "mvn",
        BuildArgs = "compile -q",
        TestCommand = "mvn",
        TestArgs = "test -q",
        LintCommand = "mvn",
        LintArgs = "checkstyle:check",
        FormatCommand = "mvn",
        FormatArgs = "spotless:check",
        RunCommand = "mvn",
        RunArgs = "exec:java -Dexec.mainClass={project}",
        PackageManager = "mvn",
        PatchManagerInstall = "dependency:copy -Dartifact={package}",
        PatchManagerLockFile = "pom.xml",
        ProjectFilePatterns = new[] { "pom.xml", "build.gradle", "build.gradle.kts" },
        SourceExtensions = new[] { ".java", ".kt", ".groovy", ".scala" },
        TestDirPattern = "src/test",
        BuildDirPattern = "src/main"
    };

    public static ProjectSpec Generic => new()
    {
        Language = "generic",
        BuildCommand = "",
        BuildArgs = "",
        TestCommand = "",
        TestArgs = "",
        LintCommand = "",
        LintArgs = "",
        FormatCommand = "",
        FormatArgs = "",
        RunCommand = "",
        RunArgs = "",
        PackageManager = "",
        PatchManagerInstall = "",
        PatchManagerLockFile = "",
        ProjectFilePatterns = Array.Empty<string>(),
        SourceExtensions = Array.Empty<string>(),
        TestDirPattern = "tests",
        BuildDirPattern = "src"
    };

    public static IReadOnlyList<ProjectSpec> AllPresets => new[]
    {
        Dotnet, Node, Python, Go, Rust, Java
    };
}
