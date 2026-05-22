using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LTAI.Tools.CodeGraph;

/// <summary>
/// 代码实体统一表示 (跨语言)
/// </summary>
public record CodeSymbol(
    string Id,
    string Name,
    string File,
    string Language,
    SymbolKind Kind,
    int Line,
    int EndLine,
    string? ParentClass,
    List<string> Dependencies,
    List<string> Dependents,
    double Complexity,
    string? SourceCode);

public enum SymbolKind { Function, Class, Module, Import, Interface, Struct, Variable, Type }

/// <summary>
/// 语言解析器统一接口
/// </summary>
public interface ILanguageParser
{
    /// <summary>
    /// 支持的文件扩展名 (如 .cs, .py, .ts)
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// 语言名称
    /// </summary>
    string LanguageName { get; }

    /// <summary>
    /// 解析单个文件，返回代码符号列表
    /// </summary>
    Task<List<CodeSymbol>> ParseFileAsync(string filePath, string content);

    /// <summary>
    /// 提取依赖关系 (imports/requires)
    /// </summary>
    List<string> ExtractImports(string content);
}

/// <summary>
/// 语言统计信息
/// </summary>
public record LanguageStats(string Language, int FileCount, int LinesOfCode, List<string> Files);

/// <summary>
/// 语言解析器注册表 - 支持懒加载与按需激活
/// </summary>
public sealed class LanguageParserRegistry
{
    private readonly Dictionary<string, ILanguageParser> _parsers = new();
    private readonly Dictionary<string, Type> _langToType = new();
    private readonly Dictionary<string, string> _extensionToLang = new();
    private readonly Dictionary<string, List<string>> _langToExtensions = new();

    public LanguageParserRegistry()
    {
        Register<CSharpParser>();
        Register<PythonParser>();
        Register<TypeScriptParser>();
        Register<JavaScriptParser>();
        Register<GoParser>();
        Register<RustParser>();
        Register<JavaParser>();
        Register<CppParser>();
        Register<PhpParser>();
        Register<RubyParser>();
        Register<SwiftParser>();
        Register<KotlinParser>();
        Register<VueParser>();
    }

    private void Register<T>() where T : ILanguageParser, new()
    {
        var temp = new T();
        var type = typeof(T);
        _langToType[temp.LanguageName] = type;
        _langToExtensions[temp.LanguageName] = new List<string>(temp.SupportedExtensions);

        foreach (var ext in temp.SupportedExtensions)
        {
            _extensionToLang[ext.ToLowerInvariant()] = temp.LanguageName;
        }
    }

    /// <summary>
    /// 获取或创建解析器实例 (懒加载)
    /// </summary>
    public ILanguageParser? GetParserForFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (_extensionToLang.TryGetValue(ext, out var lang))
        {
            if (!_parsers.TryGetValue(lang, out var parser))
            {
                if (_langToType.TryGetValue(lang, out var type))
                {
                    parser = (ILanguageParser)Activator.CreateInstance(type)!;
                    _parsers[lang] = parser;
                }
            }
            return parser;
        }
        return null;
    }

    /// <summary>
    /// 获取指定语言的解析器
    /// </summary>
    public ILanguageParser? GetParser(string languageName)
    {
        if (!_parsers.TryGetValue(languageName, out var parser))
        {
            if (_langToType.TryGetValue(languageName, out var type))
            {
                parser = (ILanguageParser)Activator.CreateInstance(type)!;
                _parsers[languageName] = parser;
            }
        }
        return parser;
    }

    public IReadOnlyList<string> SupportedLanguages => _langToType.Keys.ToList();

    public List<string> GetExtensionsForLanguage(string languageName)
    {
        return _langToExtensions.TryGetValue(languageName, out var exts) ? exts : new List<string>();
    }

    /// <summary>
    /// 快速扫描项目文件分布 (仅基于文件名，不读取内容，速度极快)
    /// </summary>
    public Dictionary<string, int> ScanFileDistribution(string rootDir)
    {
        var counts = new Dictionary<string, int>();
        var excludes = new[] { "\\obj\\", "\\bin\\", "\\node_modules\\", "\\.git\\", "\\dist\\", "\\build\\", "\\target\\", "\\vendor\\", "third_party" };
        
        try
        {
            foreach (var file in Directory.EnumerateFiles(rootDir, "*.*", SearchOption.AllDirectories))
            {
                if (excludes.Any(e => file.Contains(e, StringComparison.OrdinalIgnoreCase))) continue;
                
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (_extensionToLang.TryGetValue(ext, out var lang))
                {
                    counts[lang] = counts.GetValueOrDefault(lang, 0) + 1;
                }
            }
        }
        catch (UnauthorizedAccessException) { /* Skip inaccessible dirs */ }
        
        return counts;
    }

    /// <summary>
    /// 获取主要语言的扩展名列表 (基于文件数占比，用于按需索引)
    /// </summary>
    public List<string> GetActiveExtensions(string rootDir, double threshold = 0.01)
    {
        var distribution = ScanFileDistribution(rootDir);
        var total = distribution.Values.Sum();
        if (total == 0) return new List<string>();

        var activeLangs = distribution
            .Where(kvp => (double)kvp.Value / total >= threshold)
            .Select(kvp => kvp.Key)
            .ToList();

        var extensions = new HashSet<string>();
        foreach (var lang in activeLangs)
        {
            if (_langToExtensions.TryGetValue(lang, out var exts))
            {
                foreach (var ext in exts) extensions.Add(ext);
            }
        }
        return extensions.ToList();
    }

    /// <summary>
    /// 获取项目主要语言 (按文件数占比)
    /// </summary>
    public List<(string Language, double Percentage, int Files)> GetPrimaryLanguages(string rootDir, double threshold = 0.01)
    {
        var distribution = ScanFileDistribution(rootDir);
        var total = distribution.Values.Sum();
        if (total == 0) return new();

        return distribution
            .Select(kvp => (kvp.Key, Percentage: (double)kvp.Value / total, kvp.Value))
            .Where(x => x.Percentage >= threshold)
            .OrderByDescending(x => x.Percentage)
            .ToList();
    }

    /// <summary>
    /// 获取项目主要语言 (占比最高的语言)
    /// </summary>
    public string? GetDominantLanguage(string rootDir) => GetPrimaryLanguages(rootDir).FirstOrDefault().Language;
}
