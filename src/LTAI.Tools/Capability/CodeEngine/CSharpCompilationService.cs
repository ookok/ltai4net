using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tools.CodeEngine;

public sealed class CSharpCompilationService
{
    private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(
        LanguageVersion.Latest,
        preprocessorSymbols: new[] { "DEBUG", "TRACE" });

    private readonly ILogger<CSharpCompilationService> _logger;

    public CSharpCompilationService(ILogger<CSharpCompilationService>? logger = null)
    {
        _logger = logger ?? NullLogger<CSharpCompilationService>.Instance;
    }

    public Task<CompilationAnalysis> AnalyzeWorkspaceAsync(string workspaceRoot, CancellationToken ct = default)
    {
        var csFiles = CollectCsFiles(workspaceRoot);
        if (csFiles.Count == 0)
            return Task.FromResult(new CompilationAnalysis());
        return AnalyzeFilesAsync(csFiles, ct);
    }

    public Task<CompilationAnalysis> AnalyzeFilesAsync(IEnumerable<string> csFiles, CancellationToken ct = default)
    {
        var files = csFiles.ToList();
        var refs = GatherReferences(Path.GetDirectoryName(files.FirstOrDefault()) ?? ".");

        var compilation = CreateCompilation(files, refs, "WorkspaceAnalysis");
        var diagnostics = ExtractAllDiagnostics(compilation);

        var result = new CompilationAnalysis
        {
            TotalFiles = files.Count,
            Diagnostics = diagnostics,
            ErrorCount = diagnostics.Count(d => d.Severity == "Error"),
            WarningCount = diagnostics.Count(d => d.Severity == "Warning"),
        };

        _logger.LogInformation("CSharpCompilation analyzed {FileCount} files: {Errors} errors, {Warnings} warnings",
            files.Count, result.ErrorCount, result.WarningCount);

        return Task.FromResult(result);
    }

    private static List<string> CollectCsFiles(string root)
    {
        var csprojFiles = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories);
        var csFiles = new List<string>();
        foreach (var csproj in csprojFiles)
        {
            var dir = Path.GetDirectoryName(csproj)!;
            csFiles.AddRange(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories));
        }
        if (csFiles.Count == 0)
            csFiles.AddRange(Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories));

        csFiles = csFiles.Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\")
            && !f.Contains("\\node_modules\\")).ToList();

        return csFiles;
    }

    private static List<MetadataReference> GatherReferences(string root)
    {
        var refs = new List<MetadataReference>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (trustedPlatformAssemblies != null)
        {
            foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator))
            {
                if (File.Exists(path) && added.Add(Path.GetFileNameWithoutExtension(path)))
                    refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        var objDirs = Directory.GetDirectories(root, "obj", SearchOption.AllDirectories);
        foreach (var objDir in objDirs)
        {
            try
            {
                var dlls = Directory.GetFiles(objDir, "*.dll", SearchOption.AllDirectories);
                foreach (var dll in dlls)
                {
                    if (added.Add(Path.GetFileNameWithoutExtension(dll)))
                    {
                        try { refs.Add(MetadataReference.CreateFromFile(dll)); }
                        catch { /* intentional: cleanup may fail */ }
                    }
                }
            }
            catch { /* intentional: cleanup may fail */ }
        }

        var binDirs = Directory.GetDirectories(root, "bin", SearchOption.AllDirectories);
        foreach (var binDir in binDirs)
        {
            try
            {
                var dlls = Directory.GetFiles(binDir, "*.dll", SearchOption.AllDirectories);
                foreach (var dll in dlls)
                {
                    if (added.Add(Path.GetFileNameWithoutExtension(dll)))
                    {
                        try { refs.Add(MetadataReference.CreateFromFile(dll)); }
                        catch { /* intentional: cleanup may fail */ }
                    }
                }
            }
            catch { /* intentional: cleanup may fail */ }
        }

        return refs;
    }

    private static CSharpCompilation CreateCompilation(
        IEnumerable<string> csFiles,
        IEnumerable<MetadataReference> references,
        string assemblyName)
    {
        var syntaxTrees = new List<SyntaxTree>();
        foreach (var file in csFiles)
        {
            try
            {
                var source = File.ReadAllText(file);
                var tree = CSharpSyntaxTree.ParseText(source, ParseOptions, file);
                syntaxTrees.Add(tree);
            }
            catch { /* intentional: cleanup may fail */ }
        }

        return CSharpCompilation.Create(
            assemblyName,
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                warningLevel: 4));
    }

    private static List<CompilationDiagnostic> ExtractAllDiagnostics(CSharpCompilation compilation)
    {
        var diags = new List<CompilationDiagnostic>();

        foreach (var d in compilation.GetDiagnostics())
        {
            var lineSpan = d.Location.GetLineSpan();
            diags.Add(new CompilationDiagnostic
            {
                FilePath = lineSpan.Path ?? "",
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                EndColumn = lineSpan.EndLinePosition.Character + 1,
                Message = d.GetMessage(),
                Code = d.Id,
                Severity = d.Severity.ToString(),
                WarningLevel = d.WarningLevel,
                IsSuppressed = d.IsSuppressed,
            });
        }

        return diags;
    }
}

public sealed class CompilationAnalysis
{
    public int TotalFiles { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public List<CompilationDiagnostic> Diagnostics { get; set; } = [];

    public string ToPromptContext(int maxDiagnostics = 20)
    {
        if (Diagnostics.Count == 0)
            return "## Roslyn Diagnostics\nNo compilation issues detected.\n";

        var sb = new StringBuilder();
        sb.AppendLine($"## Roslyn Diagnostics ({ErrorCount} errors, {WarningCount} warnings)");

        var topErrors = Diagnostics.Where(d => d.Severity == "Error").Take(maxDiagnostics / 2).ToList();
        var topWarnings = Diagnostics.Where(d => d.Severity == "Warning").Take(maxDiagnostics / 2).ToList();

        foreach (var d in topErrors.Concat(topWarnings).Take(maxDiagnostics))
        {
            var file = Path.GetFileName(d.FilePath);
            sb.AppendLine($"- [{d.Severity}] `{d.Code}` {file}:{d.Line}:{d.Column} — {d.Message}");
        }

        if (Diagnostics.Count > maxDiagnostics)
            sb.AppendLine($"... and {Diagnostics.Count - maxDiagnostics} more");

        return sb.ToString();
    }
}

public sealed class CompilationDiagnostic
{
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
    public string Message { get; set; } = "";
    public string Code { get; set; } = "";
    public string Severity { get; set; } = "";
    public int WarningLevel { get; set; }
    public bool IsSuppressed { get; set; }
}
