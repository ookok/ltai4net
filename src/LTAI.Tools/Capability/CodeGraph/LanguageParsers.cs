using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LTAI.Tools.CodeGraph;

/// <summary>
/// C# 解析器实现
/// </summary>
public sealed class CSharpParser : ILanguageParser
{
    public IReadOnlyList<string> SupportedExtensions => new[] { ".cs" };
    public string LanguageName => "C#";

    public Task<List<CodeSymbol>> ParseFileAsync(string filePath, string content)
    {
        var symbols = new List<CodeSymbol>();
        var relativePath = LanguageParserHelper.GetRelativePath(filePath);
        var lines = content.Split('\n');

        var classStack = new Stack<(string name, int line)>();
        var currentClass = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // 类检测
            var classMatch = Regex.Match(line, @"(?:public|internal|static|sealed|partial)?\s*class\s+(\w+)");
            if (classMatch.Success)
            {
                var className = classMatch.Groups[1].Value;
                classStack.Push((className, i + 1));
                currentClass = string.Join(".", classStack.Select(c => c.name));
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:class:{className}:{i+1}",
                    Name: className,
                    File: relativePath,
                    Language: "C#",
                    Kind: SymbolKind.Class,
                    Line: i + 1,
                    EndLine: i + 50,
                    ParentClass: null,
                    Dependencies: new(),
                    Dependents: new(),
                    Complexity: 0,
                    SourceCode: null));
            }

            // 方法检测
            var methodMatch = Regex.Match(line, @"(?:public|private|protected|internal|static|async|virtual|override|abstract)\s+(?:[\w<>[\],]+\s+)?(\w+)\s*\(");
            if (methodMatch.Success)
            {
                var methodName = methodMatch.Groups[1].Value;
                if (methodName is not ("if" or "while" or "for" or "switch" or "catch" or "lock" or "using"))
                {
                    symbols.Add(new CodeSymbol(
                        Id: $"{relativePath}:method:{methodName}:{i+1}",
                        Name: methodName,
                        File: relativePath,
                        Language: "C#",
                        Kind: SymbolKind.Function,
                        Line: i + 1,
                        EndLine: i + 20,
                        ParentClass: currentClass,
                        Dependencies: ExtractMethodCalls(lines, i),
                        Dependents: new(),
                        Complexity: CountBranches(lines, i),
                        SourceCode: null));
                }
            }
        }

        return Task.FromResult(symbols);
    }

    public List<string> ExtractImports(string content)
    {
        var imports = new List<string>();
        var matches = Regex.Matches(content, @"^using\s+([\w.]+)", RegexOptions.Multiline);
        foreach (Match m in matches)
            imports.Add(m.Groups[1].Value);
        return imports;
    }

    private static List<string> ExtractMethodCalls(string[] lines, int methodStart)
    {
        var calls = new List<string>();
        var end = Math.Min(methodStart + 30, lines.Length);
        for (var i = methodStart; i < end; i++)
        {
            var callMatches = Regex.Matches(lines[i], @"\b(\w+)\.(\w+)\s*\(");
            foreach (Match m in callMatches)
                calls.Add(m.Groups[2].Value);
        }
        return calls;
    }

    private static double CountBranches(string[] lines, int start)
    {
        var count = 0;
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
            count += Regex.Matches(lines[i], @"\b(if|else|for|while|switch|case|catch|when|&&|\|\|)\b").Count;
        return count;
    }
}

/// <summary>
/// Python 解析器实现
/// </summary>
public sealed class PythonParser : ILanguageParser
{
    public IReadOnlyList<string> SupportedExtensions => new[] { ".py" };
    public string LanguageName => "Python";

    public Task<List<CodeSymbol>> ParseFileAsync(string filePath, string content)
    {
        var symbols = new List<CodeSymbol>();
        var relativePath = LanguageParserHelper.GetRelativePath(filePath);
        var lines = content.Split('\n');
        var currentClass = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // 类检测
            var classMatch = Regex.Match(line, @"^class\s+(\w+)");
            if (classMatch.Success)
            {
                currentClass = classMatch.Groups[1].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:class:{currentClass}:{i+1}",
                    Name: currentClass,
                    File: relativePath,
                    Language: "Python",
                    Kind: SymbolKind.Class,
                    Line: i + 1,
                    EndLine: i + 50,
                    ParentClass: null,
                    Dependencies: new(),
                    Dependents: new(),
                    Complexity: 0,
                    SourceCode: null));
            }

            // 函数检测
            var funcMatch = Regex.Match(line, @"^def\s+(\w+)\s*\(");
            if (funcMatch.Success)
            {
                var funcName = funcMatch.Groups[1].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:func:{funcName}:{i+1}",
                    Name: funcName,
                    File: relativePath,
                    Language: "Python",
                    Kind: SymbolKind.Function,
                    Line: i + 1,
                    EndLine: i + 20,
                    ParentClass: currentClass,
                    Dependencies: ExtractPythonCalls(lines, i),
                    Dependents: new(),
                    Complexity: CountPythonBranches(lines, i),
                    SourceCode: null));
            }
        }

        return Task.FromResult(symbols);
    }

    public List<string> ExtractImports(string content)
    {
        var imports = new List<string>();
        var matches = Regex.Matches(content, @"^(?:import|from)\s+([\w.]+)", RegexOptions.Multiline);
        foreach (Match m in matches)
            imports.Add(m.Groups[1].Value);
        return imports;
    }

    private static List<string> ExtractPythonCalls(string[] lines, int start)
    {
        var calls = new List<string>();
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
        {
            var callMatches = Regex.Matches(lines[i], @"\b(\w+)\.(\w+)\s*\(");
            foreach (Match m in callMatches)
                calls.Add(m.Groups[2].Value);
        }
        return calls;
    }

    private static double CountPythonBranches(string[] lines, int start)
    {
        var count = 0;
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
            count += Regex.Matches(lines[i], @"\b(if|elif|else|for|while|try|except|and|or)\b").Count;
        return count;
    }
}

/// <summary>
/// TypeScript 解析器实现
/// </summary>
public sealed class TypeScriptParser : ILanguageParser
{
    public IReadOnlyList<string> SupportedExtensions => new[] { ".ts", ".tsx" };
    public string LanguageName => "TypeScript";

    public Task<List<CodeSymbol>> ParseFileAsync(string filePath, string content)
    {
        var symbols = new List<CodeSymbol>();
        var relativePath = LanguageParserHelper.GetRelativePath(filePath);
        var lines = content.Split('\n');
        var currentClass = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // 类/接口检测
            var classMatch = Regex.Match(line, @"(?:export\s+)?(?:abstract\s+)?(?:class|interface)\s+(\w+)");
            if (classMatch.Success)
            {
                currentClass = classMatch.Groups[1].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:class:{currentClass}:{i+1}",
                    Name: currentClass,
                    File: relativePath,
                    Language: "TypeScript",
                    Kind: SymbolKind.Class,
                    Line: i + 1,
                    EndLine: i + 50,
                    ParentClass: null,
                    Dependencies: new(),
                    Dependents: new(),
                    Complexity: 0,
                    SourceCode: null));
            }

            // 函数检测
            var funcMatch = Regex.Match(line, @"(?:export\s+)?(?:async\s+)?function\s+(\w+)\s*\(");
            if (!funcMatch.Success)
                funcMatch = Regex.Match(line, @"(?:const|let|var)\s+(\w+)\s*=\s*(?:async\s+)?\(");
            
            if (funcMatch.Success)
            {
                var funcName = funcMatch.Groups[1].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:func:{funcName}:{i+1}",
                    Name: funcName,
                    File: relativePath,
                    Language: "TypeScript",
                    Kind: SymbolKind.Function,
                    Line: i + 1,
                    EndLine: i + 20,
                    ParentClass: currentClass,
                    Dependencies: ExtractTSCalls(lines, i),
                    Dependents: new(),
                    Complexity: CountTSBranches(lines, i),
                    SourceCode: null));
            }
        }

        return Task.FromResult(symbols);
    }

    public List<string> ExtractImports(string content)
    {
        var imports = new List<string>();
        var matches = Regex.Matches(content, @"import\s+(?:.*from\s+)?['""]([^'""]+)['""]", RegexOptions.Multiline);
        foreach (Match m in matches)
            imports.Add(m.Groups[1].Value);
        return imports;
    }

    private static List<string> ExtractTSCalls(string[] lines, int start)
    {
        var calls = new List<string>();
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
        {
            var callMatches = Regex.Matches(lines[i], @"\b(\w+)\.(\w+)\s*\(");
            foreach (Match m in callMatches)
                calls.Add(m.Groups[2].Value);
        }
        return calls;
    }

    private static double CountTSBranches(string[] lines, int start)
    {
        var count = 0;
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
            count += Regex.Matches(lines[i], @"\b(if|else|for|while|switch|case|catch|&&|\|\|)\b").Count;
        return count;
    }
}

/// <summary>
/// JavaScript 解析器实现
/// </summary>
public sealed class JavaScriptParser : ILanguageParser
{
    public IReadOnlyList<string> SupportedExtensions => new[] { ".js", ".jsx", ".mjs" };
    public string LanguageName => "JavaScript";

    public Task<List<CodeSymbol>> ParseFileAsync(string filePath, string content)
    {
        var symbols = new List<CodeSymbol>();
        var relativePath = LanguageParserHelper.GetRelativePath(filePath);
        var lines = content.Split('\n');
        var currentClass = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // 类检测
            var classMatch = Regex.Match(line, @"(?:export\s+)?(?:class)\s+(\w+)");
            if (classMatch.Success)
            {
                currentClass = classMatch.Groups[1].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:class:{currentClass}:{i+1}",
                    Name: currentClass,
                    File: relativePath,
                    Language: "JavaScript",
                    Kind: SymbolKind.Class,
                    Line: i + 1,
                    EndLine: i + 50,
                    ParentClass: null,
                    Dependencies: new(),
                    Dependents: new(),
                    Complexity: 0,
                    SourceCode: null));
            }

            // 函数检测
            var funcMatch = Regex.Match(line, @"(?:export\s+)?(?:async\s+)?function\s+(\w+)\s*\(");
            if (!funcMatch.Success)
                funcMatch = Regex.Match(line, @"(?:const|let|var)\s+(\w+)\s*=\s*(?:async\s+)?\(");
            
            if (funcMatch.Success)
            {
                var funcName = funcMatch.Groups[1].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:func:{funcName}:{i+1}",
                    Name: funcName,
                    File: relativePath,
                    Language: "JavaScript",
                    Kind: SymbolKind.Function,
                    Line: i + 1,
                    EndLine: i + 20,
                    ParentClass: currentClass,
                    Dependencies: ExtractJSCalls(lines, i),
                    Dependents: new(),
                    Complexity: CountJSBranches(lines, i),
                    SourceCode: null));
            }
        }

        return Task.FromResult(symbols);
    }

    public List<string> ExtractImports(string content)
    {
        var imports = new List<string>();
        var matches = Regex.Matches(content, @"import\s+(?:.*from\s+)?['""]([^'""]+)['""]", RegexOptions.Multiline);
        foreach (Match m in matches)
            imports.Add(m.Groups[1].Value);
        return imports;
    }

    private static List<string> ExtractJSCalls(string[] lines, int start)
    {
        var calls = new List<string>();
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
        {
            var callMatches = Regex.Matches(lines[i], @"\b(\w+)\.(\w+)\s*\(");
            foreach (Match m in callMatches)
                calls.Add(m.Groups[2].Value);
        }
        return calls;
    }

    private static double CountJSBranches(string[] lines, int start)
    {
        var count = 0;
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
            count += Regex.Matches(lines[i], @"\b(if|else|for|while|switch|case|catch|&&|\|\|)\b").Count;
        return count;
    }
}

/// <summary>
/// Go 解析器实现
/// </summary>
public sealed class GoParser : ILanguageParser
{
    public IReadOnlyList<string> SupportedExtensions => new[] { ".go" };
    public string LanguageName => "Go";

    public Task<List<CodeSymbol>> ParseFileAsync(string filePath, string content)
    {
        var symbols = new List<CodeSymbol>();
        var relativePath = LanguageParserHelper.GetRelativePath(filePath);
        var lines = content.Split('\n');
        var currentPackage = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // 包检测
            var pkgMatch = Regex.Match(line, @"^package\s+(\w+)");
            if (pkgMatch.Success)
                currentPackage = pkgMatch.Groups[1].Value;

            // 函数检测
            var funcMatch = Regex.Match(line, @"^func\s+(?:\(\w+\s+\*?\w+\)\s+)?(\w+)\s*\(");
            if (funcMatch.Success)
            {
                var funcName = funcMatch.Groups[1].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:func:{funcName}:{i+1}",
                    Name: funcName,
                    File: relativePath,
                    Language: "Go",
                    Kind: SymbolKind.Function,
                    Line: i + 1,
                    EndLine: i + 20,
                    ParentClass: currentPackage,
                    Dependencies: ExtractGoCalls(lines, i),
                    Dependents: new(),
                    Complexity: CountGoBranches(lines, i),
                    SourceCode: null));
            }

            // 结构体检测
            var structMatch = Regex.Match(line, @"^type\s+(\w+)\s+struct");
            if (structMatch.Success)
            {
                var structName = structMatch.Groups[1].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:struct:{structName}:{i+1}",
                    Name: structName,
                    File: relativePath,
                    Language: "Go",
                    Kind: SymbolKind.Struct,
                    Line: i + 1,
                    EndLine: i + 30,
                    ParentClass: currentPackage,
                    Dependencies: new(),
                    Dependents: new(),
                    Complexity: 0,
                    SourceCode: null));
            }
        }

        return Task.FromResult(symbols);
    }

    public List<string> ExtractImports(string content)
    {
        var imports = new List<string>();
        var matches = Regex.Matches(content, @"import\s+(?:\(\s*)?""([^""]+)""", RegexOptions.Multiline);
        foreach (Match m in matches)
            imports.Add(m.Groups[1].Value);
        return imports;
    }

    private static List<string> ExtractGoCalls(string[] lines, int start)
    {
        var calls = new List<string>();
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
        {
            var callMatches = Regex.Matches(lines[i], @"\b(\w+)\.(\w+)\s*\(");
            foreach (Match m in callMatches)
                calls.Add(m.Groups[2].Value);
        }
        return calls;
    }

    private static double CountGoBranches(string[] lines, int start)
    {
        var count = 0;
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
            count += Regex.Matches(lines[i], @"\b(if|else|for|switch|case|range|&&|\|\|)\b").Count;
        return count;
    }
}

/// <summary>
/// Rust 解析器实现
/// </summary>
public sealed class RustParser : ILanguageParser
{
    public IReadOnlyList<string> SupportedExtensions => new[] { ".rs" };
    public string LanguageName => "Rust";

    public Task<List<CodeSymbol>> ParseFileAsync(string filePath, string content)
    {
        var symbols = new List<CodeSymbol>();
        var relativePath = LanguageParserHelper.GetRelativePath(filePath);
        var lines = content.Split('\n');
        var currentMod = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // 模块检测
            var modMatch = Regex.Match(line, @"^mod\s+(\w+)");
            if (modMatch.Success)
                currentMod = modMatch.Groups[1].Value;

            // 函数检测
            var funcMatch = Regex.Match(line, @"^pub\s+(?:async\s+)?fn\s+(\w+)\s*[\(<]");
            if (funcMatch.Success)
            {
                var funcName = funcMatch.Groups[1].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:func:{funcName}:{i+1}",
                    Name: funcName,
                    File: relativePath,
                    Language: "Rust",
                    Kind: SymbolKind.Function,
                    Line: i + 1,
                    EndLine: i + 20,
                    ParentClass: currentMod,
                    Dependencies: ExtractRustCalls(lines, i),
                    Dependents: new(),
                    Complexity: CountRustBranches(lines, i),
                    SourceCode: null));
            }

            // 结构体检测
            var structMatch = Regex.Match(line, @"^pub\s+struct\s+(\w+)");
            if (structMatch.Success)
            {
                var structName = structMatch.Groups[1].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:struct:{structName}:{i+1}",
                    Name: structName,
                    File: relativePath,
                    Language: "Rust",
                    Kind: SymbolKind.Struct,
                    Line: i + 1,
                    EndLine: i + 30,
                    ParentClass: currentMod,
                    Dependencies: new(),
                    Dependents: new(),
                    Complexity: 0,
                    SourceCode: null));
            }
        }

        return Task.FromResult(symbols);
    }

    public List<string> ExtractImports(string content)
    {
        var imports = new List<string>();
        var matches = Regex.Matches(content, @"^use\s+([\w:]+)", RegexOptions.Multiline);
        foreach (Match m in matches)
            imports.Add(m.Groups[1].Value);
        return imports;
    }

    private static List<string> ExtractRustCalls(string[] lines, int start)
    {
        var calls = new List<string>();
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
        {
            var callMatches = Regex.Matches(lines[i], @"\b(\w+)::(\w+)\s*\(");
            foreach (Match m in callMatches)
                calls.Add(m.Groups[2].Value);
        }
        return calls;
    }

    private static double CountRustBranches(string[] lines, int start)
    {
        var count = 0;
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
            count += Regex.Matches(lines[i], @"\b(if|else|for|while|match|loop|&&|\|\|)\b").Count;
        return count;
    }
}

/// <summary>
/// Java 解析器实现
/// </summary>
public sealed class JavaParser : ILanguageParser
{
    public IReadOnlyList<string> SupportedExtensions => new[] { ".java" };
    public string LanguageName => "Java";

    public Task<List<CodeSymbol>> ParseFileAsync(string filePath, string content)
    {
        var symbols = new List<CodeSymbol>();
        var relativePath = LanguageParserHelper.GetRelativePath(filePath);
        var lines = content.Split('\n');
        var currentClass = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // 类检测
            var classMatch = Regex.Match(line, @"(?:public|private|protected)?\s*(?:abstract\s+)?class\s+(\w+)");
            if (classMatch.Success)
            {
                currentClass = classMatch.Groups[1].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:class:{currentClass}:{i+1}",
                    Name: currentClass,
                    File: relativePath,
                    Language: "Java",
                    Kind: SymbolKind.Class,
                    Line: i + 1,
                    EndLine: i + 50,
                    ParentClass: null,
                    Dependencies: new(),
                    Dependents: new(),
                    Complexity: 0,
                    SourceCode: null));
            }

            // 方法检测
            var methodMatch = Regex.Match(line, @"(?:public|private|protected|static|final|abstract)\s+(?:[\w<>[\],]+\s+)?(\w+)\s*\(");
            if (methodMatch.Success)
            {
                var methodName = methodMatch.Groups[1].Value;
                if (methodName is not ("if" or "while" or "for" or "switch" or "catch"))
                {
                    symbols.Add(new CodeSymbol(
                        Id: $"{relativePath}:method:{methodName}:{i+1}",
                        Name: methodName,
                        File: relativePath,
                        Language: "Java",
                        Kind: SymbolKind.Function,
                        Line: i + 1,
                        EndLine: i + 20,
                        ParentClass: currentClass,
                        Dependencies: ExtractJavaCalls(lines, i),
                        Dependents: new(),
                        Complexity: CountJavaBranches(lines, i),
                        SourceCode: null));
                }
            }
        }

        return Task.FromResult(symbols);
    }

    public List<string> ExtractImports(string content)
    {
        var imports = new List<string>();
        var matches = Regex.Matches(content, @"^import\s+([\w.]+)", RegexOptions.Multiline);
        foreach (Match m in matches)
            imports.Add(m.Groups[1].Value);
        return imports;
    }

    private static List<string> ExtractJavaCalls(string[] lines, int start)
    {
        var calls = new List<string>();
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
        {
            var callMatches = Regex.Matches(lines[i], @"\b(\w+)\.(\w+)\s*\(");
            foreach (Match m in callMatches)
                calls.Add(m.Groups[2].Value);
        }
        return calls;
    }

    private static double CountJavaBranches(string[] lines, int start)
    {
        var count = 0;
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
            count += Regex.Matches(lines[i], @"\b(if|else|for|while|switch|case|catch|&&|\|\|)\b").Count;
        return count;
    }
}

/// <summary>
/// C/C++ 解析器实现
/// </summary>
public sealed class CppParser : ILanguageParser
{
    public IReadOnlyList<string> SupportedExtensions => new[] { ".c", ".cpp", ".h", ".hpp", ".cc", ".cxx" };
    public string LanguageName => "C++";

    public Task<List<CodeSymbol>> ParseFileAsync(string filePath, string content)
    {
        var symbols = new List<CodeSymbol>();
        var relativePath = LanguageParserHelper.GetRelativePath(filePath);
        var lines = content.Split('\n');
        var currentNamespace = "";
        var isHeader = filePath.EndsWith(".h") || filePath.EndsWith(".hpp");

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            var nsMatch = Regex.Match(line, @"^namespace\s+(\w+)");
            if (nsMatch.Success) currentNamespace = nsMatch.Groups[1].Value;

            // 类/结构体
            var classMatch = Regex.Match(line, @"^(?:class|struct)\s+(\w+)");
            if (classMatch.Success)
            {
                var name = classMatch.Groups[1].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:class:{name}:{i+1}",
                    Name: name, File: relativePath, Language: "C++",
                    Kind: SymbolKind.Class, Line: i + 1, EndLine: i + 50,
                    ParentClass: currentNamespace, Dependencies: new(), Dependents: new(),
                    Complexity: 0, SourceCode: null));
            }

            // 函数
            var funcMatch = Regex.Match(line, @"^(?:(?:static|inline|virtual|explicit|constexpr|extern)\s+)*[\w:*&<>,\s]+\s+(\w+)\s*\(");
            if (funcMatch.Success)
            {
                var name = funcMatch.Groups[1].Value;
                if (name is not ("if" or "while" or "for" or "switch" or "catch" or "return" or "sizeof" or "typedef" or "namespace" or "class" or "struct"))
                {
                    symbols.Add(new CodeSymbol(
                        Id: $"{relativePath}:func:{name}:{i+1}",
                        Name: name, File: relativePath, Language: "C++",
                        Kind: SymbolKind.Function, Line: i + 1, EndLine: i + 20,
                        ParentClass: currentNamespace, Dependencies: ExtractCppCalls(lines, i),
                        Dependents: new(), Complexity: CountCppBranches(lines, i),
                        SourceCode: null));
                }
            }
        }

        return Task.FromResult(symbols);
    }

    public List<string> ExtractImports(string content)
    {
        var imports = new List<string>();
        var matches = Regex.Matches(content, @"^#include\s+[<""']([^>""']+)[>""']", RegexOptions.Multiline);
        foreach (Match m in matches) imports.Add(m.Groups[1].Value);
        return imports;
    }

    private static List<string> ExtractCppCalls(string[] lines, int start)
    {
        var calls = new List<string>();
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
        {
            var m = Regex.Matches(lines[i], @"\b(\w+)(?:\.|::)(\w+)\s*\(");
            foreach (Match match in m) calls.Add(match.Groups[2].Value);
        }
        return calls;
    }
    private static double CountCppBranches(string[] lines, int start)
    {
        var count = 0;
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
            count += Regex.Matches(lines[i], @"\b(if|else|for|while|switch|case|catch|&&|\|\|)\b").Count;
        return count;
    }
}

/// <summary>
/// PHP 解析器实现
/// </summary>
public sealed class PhpParser : ILanguageParser
{
    public IReadOnlyList<string> SupportedExtensions => new[] { ".php" };
    public string LanguageName => "PHP";

    public Task<List<CodeSymbol>> ParseFileAsync(string filePath, string content)
    {
        var symbols = new List<CodeSymbol>();
        var relativePath = LanguageParserHelper.GetRelativePath(filePath);
        var lines = content.Split('\n');
        var currentClass = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var classMatch = Regex.Match(line, @"^(?:abstract|final)?\s*(?:class|trait|interface)\s+(\w+)");
            if (classMatch.Success)
            {
                currentClass = classMatch.Groups[1].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:class:{currentClass}:{i+1}",
                    Name: currentClass, File: relativePath, Language: "PHP",
                    Kind: SymbolKind.Class, Line: i + 1, EndLine: i + 50,
                    ParentClass: null, Dependencies: new(), Dependents: new(),
                    Complexity: 0, SourceCode: null));
            }

            var funcMatch = Regex.Match(line, @"^(?:public|private|protected|static|abstract|final)\s+function\s+(\w+)\s*\(");
            if (!funcMatch.Success)
                funcMatch = Regex.Match(line, @"^function\s+(\w+)\s*\(");
            if (funcMatch.Success)
            {
                var name = funcMatch.Groups[1].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:func:{name}:{i+1}",
                    Name: name, File: relativePath, Language: "PHP",
                    Kind: SymbolKind.Function, Line: i + 1, EndLine: i + 20,
                    ParentClass: currentClass, Dependencies: ExtractPhpCalls(lines, i),
                    Dependents: new(), Complexity: CountPhpBranches(lines, i),
                    SourceCode: null));
            }
        }

        return Task.FromResult(symbols);
    }

    public List<string> ExtractImports(string content)
    {
        var imports = new List<string>();
        var matches = Regex.Matches(content, @"^(?:use|require|include)\s+['""]?([^;'""\s]+)", RegexOptions.Multiline);
        foreach (Match m in matches) imports.Add(m.Groups[1].Value);
        return imports;
    }

    private static List<string> ExtractPhpCalls(string[] lines, int start)
    {
        var calls = new List<string>();
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
        {
            var m = Regex.Matches(lines[i], @"\b(\w+)\s*->\s*(\w+)\s*\(");
            foreach (Match match in m) calls.Add(match.Groups[2].Value);
        }
        return calls;
    }
    private static double CountPhpBranches(string[] lines, int start)
    {
        var count = 0;
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
            count += Regex.Matches(lines[i], @"\b(if|elseif|else|for|foreach|while|switch|case|catch|&&|\|\|)\b").Count;
        return count;
    }
}

/// <summary>
/// Ruby 解析器实现
/// </summary>
public sealed class RubyParser : ILanguageParser
{
    public IReadOnlyList<string> SupportedExtensions => new[] { ".rb" };
    public string LanguageName => "Ruby";

    public Task<List<CodeSymbol>> ParseFileAsync(string filePath, string content)
    {
        var symbols = new List<CodeSymbol>();
        var relativePath = LanguageParserHelper.GetRelativePath(filePath);
        var lines = content.Split('\n');
        var currentClass = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var classMatch = Regex.Match(line, @"^(?:class|module)\s+([A-Z]\w*)");
            if (classMatch.Success)
            {
                currentClass = classMatch.Groups[1].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:class:{currentClass}:{i+1}",
                    Name: currentClass, File: relativePath, Language: "Ruby",
                    Kind: SymbolKind.Class, Line: i + 1, EndLine: i + 50,
                    ParentClass: null, Dependencies: new(), Dependents: new(),
                    Complexity: 0, SourceCode: null));
            }

            var defMatch = Regex.Match(line, @"^def\s+(?:self\.)?(\w+)");
            if (defMatch.Success)
            {
                var name = defMatch.Groups[1].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:func:{name}:{i+1}",
                    Name: name, File: relativePath, Language: "Ruby",
                    Kind: SymbolKind.Function, Line: i + 1, EndLine: i + 20,
                    ParentClass: currentClass, Dependencies: ExtractRubyCalls(lines, i),
                    Dependents: new(), Complexity: CountRubyBranches(lines, i),
                    SourceCode: null));
            }
        }

        return Task.FromResult(symbols);
    }

    public List<string> ExtractImports(string content)
    {
        var imports = new List<string>();
        var matches = Regex.Matches(content, @"^(?:require|require_relative|include)\s+['""]?([^'""\s]+)", RegexOptions.Multiline);
        foreach (Match m in matches) imports.Add(m.Groups[1].Value);
        return imports;
    }

    private static List<string> ExtractRubyCalls(string[] lines, int start)
    {
        var calls = new List<string>();
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
        {
            var m = Regex.Matches(lines[i], @"\.(\w+)\s*[!]?");
            foreach (Match match in m) calls.Add(match.Groups[1].Value);
        }
        return calls;
    }
    private static double CountRubyBranches(string[] lines, int start)
    {
        var count = 0;
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
            count += Regex.Matches(lines[i], @"\b(if|elsif|else|unless|for|while|until|case|when|rescue|&&|\|\|)\b").Count;
        return count;
    }
}

/// <summary>
/// Swift 解析器实现
/// </summary>
public sealed class SwiftParser : ILanguageParser
{
    public IReadOnlyList<string> SupportedExtensions => new[] { ".swift" };
    public string LanguageName => "Swift";

    public Task<List<CodeSymbol>> ParseFileAsync(string filePath, string content)
    {
        var symbols = new List<CodeSymbol>();
        var relativePath = LanguageParserHelper.GetRelativePath(filePath);
        var lines = content.Split('\n');
        var currentType = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var typeMatch = Regex.Match(line, @"^(?:public|private|internal|open|final)?\s*(class|struct|enum|protocol|extension)\s+(\w+)");
            if (typeMatch.Success)
            {
                currentType = typeMatch.Groups[2].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:class:{currentType}:{i+1}",
                    Name: currentType, File: relativePath, Language: "Swift",
                    Kind: SymbolKind.Class, Line: i + 1, EndLine: i + 50,
                    ParentClass: null, Dependencies: new(), Dependents: new(),
                    Complexity: 0, SourceCode: null));
            }

            var funcMatch = Regex.Match(line, @"^(?:public|private|internal|open|static|mutating|override|func)\s+func\s+(\w+)\s*[\(<]");
            if (!funcMatch.Success)
                funcMatch = Regex.Match(line, @"^func\s+(\w+)\s*[\(<]");
            if (funcMatch.Success)
            {
                var name = funcMatch.Groups[1].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:func:{name}:{i+1}",
                    Name: name, File: relativePath, Language: "Swift",
                    Kind: SymbolKind.Function, Line: i + 1, EndLine: i + 20,
                    ParentClass: currentType, Dependencies: ExtractSwiftCalls(lines, i),
                    Dependents: new(), Complexity: CountSwiftBranches(lines, i),
                    SourceCode: null));
            }
        }

        return Task.FromResult(symbols);
    }

    public List<string> ExtractImports(string content)
    {
        var imports = new List<string>();
        var matches = Regex.Matches(content, @"^import\s+(\w+)", RegexOptions.Multiline);
        foreach (Match m in matches) imports.Add(m.Groups[1].Value);
        return imports;
    }
    private static List<string> ExtractSwiftCalls(string[] lines, int start)
    {
        var calls = new List<string>();
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
        {
            var m = Regex.Matches(lines[i], @"\.(\w+)\s*\(");
            foreach (Match match in m) calls.Add(match.Groups[1].Value);
        }
        return calls;
    }
    private static double CountSwiftBranches(string[] lines, int start)
    {
        var count = 0;
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
            count += Regex.Matches(lines[i], @"\b(if|else|guard|for|while|repeat|switch|case|catch|&&|\|\|)\b").Count;
        return count;
    }
}

/// <summary>
/// Kotlin 解析器实现
/// </summary>
public sealed class KotlinParser : ILanguageParser
{
    public IReadOnlyList<string> SupportedExtensions => new[] { ".kt", ".kts" };
    public string LanguageName => "Kotlin";

    public Task<List<CodeSymbol>> ParseFileAsync(string filePath, string content)
    {
        var symbols = new List<CodeSymbol>();
        var relativePath = LanguageParserHelper.GetRelativePath(filePath);
        var lines = content.Split('\n');
        var currentClass = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var classMatch = Regex.Match(line, @"^(?:public|private|internal|data|sealed|abstract|open)?\s*(class|object|interface|enum class)\s+(\w+)");
            if (classMatch.Success)
            {
                currentClass = classMatch.Groups[2].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:class:{currentClass}:{i+1}",
                    Name: currentClass, File: relativePath, Language: "Kotlin",
                    Kind: SymbolKind.Class, Line: i + 1, EndLine: i + 50,
                    ParentClass: null, Dependencies: new(), Dependents: new(),
                    Complexity: 0, SourceCode: null));
            }

            var funcMatch = Regex.Match(line, @"^(?:public|private|internal|suspend|override|fun)\s+fun\s+(\w+)\s*[\(<]");
            if (!funcMatch.Success)
                funcMatch = Regex.Match(line, @"^fun\s+(\w+)\s*[\(<]");
            if (funcMatch.Success)
            {
                var name = funcMatch.Groups[1].Value;
                symbols.Add(new CodeSymbol(
                    Id: $"{relativePath}:func:{name}:{i+1}",
                    Name: name, File: relativePath, Language: "Kotlin",
                    Kind: SymbolKind.Function, Line: i + 1, EndLine: i + 20,
                    ParentClass: currentClass, Dependencies: ExtractKotlinCalls(lines, i),
                    Dependents: new(), Complexity: CountKotlinBranches(lines, i),
                    SourceCode: null));
            }
        }

        return Task.FromResult(symbols);
    }

    public List<string> ExtractImports(string content)
    {
        var imports = new List<string>();
        var matches = Regex.Matches(content, @"^import\s+([\w.]+)", RegexOptions.Multiline);
        foreach (Match m in matches) imports.Add(m.Groups[1].Value);
        return imports;
    }

    private static List<string> ExtractKotlinCalls(string[] lines, int start)
    {
        var calls = new List<string>();
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
        {
            var m = Regex.Matches(lines[i], @"\.(\w+)\s*\(");
            foreach (Match match in m) calls.Add(match.Groups[1].Value);
        }
        return calls;
    }
    private static double CountKotlinBranches(string[] lines, int start)
    {
        var count = 0;
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
            count += Regex.Matches(lines[i], @"\b(if|else|for|while|when|try|catch|&&|\|\|)\b").Count;
        return count;
    }
}

/// <summary>
/// Vue 单文件组件解析器实现
/// </summary>
public sealed class VueParser : ILanguageParser
{
    public IReadOnlyList<string> SupportedExtensions => new[] { ".vue" };
    public string LanguageName => "Vue";

    public Task<List<CodeSymbol>> ParseFileAsync(string filePath, string content)
    {
        var symbols = new List<CodeSymbol>();
        var relativePath = LanguageParserHelper.GetRelativePath(filePath);

        // 提取 script 块内容
        var scriptMatch = Regex.Match(content, @"<script[^>]*>(.*?)</script>", RegexOptions.Singleline);
        if (!scriptMatch.Success) return Task.FromResult(symbols);

        var scriptContent = scriptMatch.Groups[1].Value;
        var lines = scriptContent.Split('\n');
        var componentName = Path.GetFileNameWithoutExtension(filePath);

        // 检测组件名
        var nameMatch = Regex.Match(scriptContent, @"name:\s*['""](\w+)['""]");
        if (nameMatch.Success) componentName = nameMatch.Groups[1].Value;

        symbols.Add(new CodeSymbol(
            Id: $"{relativePath}:component:{componentName}:1",
            Name: componentName, File: relativePath, Language: "Vue",
            Kind: SymbolKind.Class, Line: 1, EndLine: lines.Length,
            ParentClass: null, Dependencies: ExtractVueImports(content),
            Dependents: new(), Complexity: 0, SourceCode: null));

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var funcMatch = Regex.Match(line, @"(?:async\s+)?(\w+)\s*\([^)]*\)\s*\{");
            if (funcMatch.Success)
            {
                var name = funcMatch.Groups[1].Value;
                if (name is not ("if" or "for" or "while" or "switch" or "function"))
                {
                    symbols.Add(new CodeSymbol(
                        Id: $"{relativePath}:method:{name}:{i+1}",
                        Name: name, File: relativePath, Language: "Vue",
                        Kind: SymbolKind.Function, Line: i + 1, EndLine: i + 20,
                        ParentClass: componentName, Dependencies: new(),
                        Dependents: new(), Complexity: CountVueBranches(lines, i),
                        SourceCode: null));
                }
            }
        }

        return Task.FromResult(symbols);
    }

    public List<string> ExtractImports(string content) => ExtractVueImports(content);

    private static List<string> ExtractVueImports(string content)
    {
        var imports = new List<string>();
        var matches = Regex.Matches(content, @"import\s+.*from\s+['""]([^'""]+)['""]", RegexOptions.Multiline);
        foreach (Match m in matches) imports.Add(m.Groups[1].Value);
        return imports;
    }

    private static double CountVueBranches(string[] lines, int start)
    {
        var count = 0;
        var end = Math.Min(start + 20, lines.Length);
        for (var i = start; i < end; i++)
            count += Regex.Matches(lines[i], @"\b(if|else|for|while|switch|case|catch|&&|\|\|)\b").Count;
        return count;
    }
}

internal static class LanguageParserHelper
{
    public static string GetRelativePath(string filePath) =>
        System.IO.Path.GetRelativePath(System.IO.Directory.GetCurrentDirectory(), filePath).Replace('\\', '/');
}
