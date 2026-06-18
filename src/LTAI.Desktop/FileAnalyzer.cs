namespace LTAI.Desktop;

/// <summary>Pure file classification logic extracted from TextPadView.
/// No UI dependencies — fully testable.</summary>
public static class FileAnalyzer
{
    private static readonly HashSet<string> CodeExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".ts", ".go", ".rs", ".java",
        ".jsx", ".tsx", ".css", ".html", ".sh", ".bash",
    };

    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".ts", ".go", ".rs", ".java", ".sh", ".bash",
        ".md", ".txt", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini",
        ".cfg", ".conf", ".css", ".html", ".htm", ".jsx", ".tsx", ".sln",
        ".csproj", ".props", ".targets", ".gitignore", ".env", ".editorconfig",
    };

    private static readonly HashSet<string> ProjectFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sln", ".csproj", ".props", ".targets",
    };

    public static bool IsCodeFile(string path) => CodeExts.Contains(Path.GetExtension(path));
    public static bool IsTextFile(string path) => TextExts.Contains(Path.GetExtension(path));
    public static bool IsProjectFile(string path) => ProjectFiles.Contains(Path.GetExtension(path));

    /// <summary>Determine the project type from a directory.</summary>
    public static string DetectProjectType(string rootDir)
    {
        if (!Directory.Exists(rootDir)) return "unknown";
        if (Directory.GetFiles(rootDir, "*.sln").Length > 0) return "dotnet";
        if (Directory.GetFiles(rootDir, "*.csproj", SearchOption.AllDirectories).Length > 0) return "dotnet";
        if (Directory.GetFiles(rootDir, "package.json").Length > 0) return "node";
        if (Directory.GetFiles(rootDir, "Cargo.toml").Length > 0) return "rust";
        if (Directory.GetFiles(rootDir, "go.mod").Length > 0) return "go";
        if (Directory.GetFiles(rootDir, "pom.xml", SearchOption.AllDirectories).Length > 0) return "java";
        if (Directory.GetFiles(rootDir, "*.py", SearchOption.TopDirectoryOnly).Length > 0) return "python";
        return "unknown";
    }

    public const long MaxEditorSize = 50 * 1024 * 1024;
}
