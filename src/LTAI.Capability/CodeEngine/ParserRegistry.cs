using Microsoft.Extensions.Logging;

namespace LTAI.Capability.CodeEngine;

public sealed class ParserRegistry
{
    private readonly Dictionary<CodeLanguage, ICodeParser> _parsers = new();
    private readonly ILoggerFactory _loggerFactory;

    public ParserRegistry(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;

        _parsers[CodeLanguage.CSharp] = new RoslynCSharpParser(
            loggerFactory.CreateLogger<RoslynCSharpParser>());

        _parsers[CodeLanguage.Python] = new TreeSitterParser(
            CodeLanguage.Python, loggerFactory.CreateLogger<TreeSitterParser>());

        _parsers[CodeLanguage.JavaScript] = new TreeSitterParser(
            CodeLanguage.JavaScript, loggerFactory.CreateLogger<TreeSitterParser>());

        _parsers[CodeLanguage.TypeScript] = new TreeSitterParser(
            CodeLanguage.TypeScript, loggerFactory.CreateLogger<TreeSitterParser>());

        _parsers[CodeLanguage.Go] = new TreeSitterParser(
            CodeLanguage.Go, loggerFactory.CreateLogger<TreeSitterParser>());

        _parsers[CodeLanguage.Rust] = new TreeSitterParser(
            CodeLanguage.Rust, loggerFactory.CreateLogger<TreeSitterParser>());

        _parsers[CodeLanguage.Java] = new TreeSitterParser(
            CodeLanguage.Java, loggerFactory.CreateLogger<TreeSitterParser>());
    }

    public ICodeParser? GetParser(CodeLanguage language)
    {
        _parsers.TryGetValue(language, out var parser);
        return parser;
    }

    public bool HasParser(CodeLanguage language) => _parsers.ContainsKey(language);

    public IReadOnlyCollection<CodeLanguage> SupportedLanguages => _parsers.Keys;
}
