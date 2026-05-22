using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Capability.CodeEngine;

public sealed class TreeSitterParser : ICodeParser
{
    private readonly CodeLanguage _language;
    private readonly ILogger<TreeSitterParser> _logger;
    private static readonly Lock GrammarLock = new();
    private static readonly Dictionary<CodeLanguage, object?> LoadedGrammars = new();
    private static readonly Dictionary<CodeLanguage, bool> GrammarAvailable = new();

    public CodeLanguage Language => _language;
    public bool SupportsDiagnostics => false;

    private record struct SNode(string Kind, int StartLine, int StartCol, int EndLine, int EndCol,
        string Text, List<SNode> Children);

    public TreeSitterParser(CodeLanguage language, ILogger<TreeSitterParser>? logger = null)
    {
        _language = language;
        _logger = logger ?? NullLogger<TreeSitterParser>.Instance;
        EnsureGrammarLoaded();
    }

    public Task<CodeParseResult> ParseAsync(string sourceCode, string? filePath = null,
        CancellationToken cancellationToken = default)
    {
        var result = ParseWithTreeSitter(sourceCode, filePath);
        _logger.LogInformation("TreeSitter parsed {Lang} {Path}: {Funcs} functions, {Classes} classes, {Imports} imports",
            _language, filePath ?? "(memory)", result.Functions.Count, result.Classes.Count, result.Imports.Count);
        return Task.FromResult(result);
    }

    private CodeParseResult ParseWithTreeSitter(string sourceCode, string? filePath)
    {
        var lines = sourceCode.Split('\n');
        var result = new CodeParseResult
        {
            Language = _language,
            FilePath = filePath ?? "",
            TotalLines = lines.Length,
            CodeLines = CountCodeLines(lines, _language),
            CommentLines = CountCommentLines(lines, _language),
            BlankLines = CountBlankLines(lines),
        };

        if (!GrammarAvailable.GetValueOrDefault(_language, false))
        {
            FallbackParse(sourceCode, result);
            return result;
        }

        try
        {
            var grammarObj = LoadedGrammars.GetValueOrDefault(_language);
            if (grammarObj == null) { FallbackParse(sourceCode, result); return result; }

            var root = DoParse(sourceCode, grammarObj);
            if (root.Children.Count == 0) { FallbackParse(sourceCode, result); return result; }

            result.Functions = ExtractFunctionsFromTree(root, _language);
            result.Classes = ExtractClassesFromTree(root, _language);
            result.Imports = ExtractImportsFromTree(root, _language);
        }
        catch
        {
            _logger.LogWarning("TreeSitter parse failed for {Lang}, falling back to regex", _language);
            FallbackParse(sourceCode, result);
        }

        return result;
    }

    private void EnsureGrammarLoaded()
    {
        if (GrammarAvailable.ContainsKey(_language)) return;

        lock (GrammarLock)
        {
            if (GrammarAvailable.ContainsKey(_language)) return;

            try
            {
                var grammarObj = LoadGrammar(_language);
                if (grammarObj != null)
                {
                    LoadedGrammars[_language] = grammarObj;
                    GrammarAvailable[_language] = true;
                }
                else
                {
                    GrammarAvailable[_language] = false;
                }
            }
            catch
            {
                GrammarAvailable[_language] = false;
                _logger.LogInformation("TreeSitter grammar not available for {Lang}, using fallback", _language);
            }
        }
    }

    private static object? LoadGrammar(CodeLanguage language)
    {
        try
        {
            var tsParserType = Type.GetType("TreeSitter.Parser, TreeSitter.DotNet");
            var tsLangType = Type.GetType("TreeSitter.Language, TreeSitter.DotNet");
            if (tsParserType == null || tsLangType == null) return null;

            object? langObj = language switch
            {
                CodeLanguage.Python => Activator.CreateInstance(Type.GetType("TreeSitter.Python.Language, TreeSitter.DotNet.Python") ?? tsLangType),
                CodeLanguage.JavaScript => Activator.CreateInstance(Type.GetType("TreeSitter.JavaScript.Language, TreeSitter.DotNet.JavaScript") ?? tsLangType),
                CodeLanguage.TypeScript => Activator.CreateInstance(Type.GetType("TreeSitter.TypeScript.Language, TreeSitter.DotNet.TypeScript") ?? tsLangType),
                CodeLanguage.Go => Activator.CreateInstance(Type.GetType("TreeSitter.Go.Language, TreeSitter.DotNet.Go") ?? tsLangType),
                CodeLanguage.Rust => Activator.CreateInstance(Type.GetType("TreeSitter.Rust.Language, TreeSitter.DotNet.Rust") ?? tsLangType),
                CodeLanguage.Java => Activator.CreateInstance(Type.GetType("TreeSitter.Java.Language, TreeSitter.DotNet.Java") ?? tsLangType),
                _ => null,
            };

            return langObj;
        }
        catch
        {
            return null;
        }
    }

    private SNode DoParse(string sourceCode, object grammarObj)
    {
        try
        {
            var tsParserType = Type.GetType("TreeSitter.Parser, TreeSitter.DotNet");
            if (tsParserType == null) return default;

            var parser = Activator.CreateInstance(tsParserType);
            if (parser == null) return default;

            var setLangMethod = tsParserType.GetMethod("SetLanguage");
            setLangMethod?.Invoke(parser, new[] { grammarObj });

            var parseMethod = tsParserType.GetMethod("Parse", new[] { typeof(string) });
            var tree = parseMethod?.Invoke(parser, new object[] { sourceCode });
            if (tree == null) return default;

            var rootNodeProp = tree.GetType().GetProperty("Root") ?? tree.GetType().GetProperty("RootNode");
            var rootVal = rootNodeProp?.GetValue(tree);
            if (rootVal == null) return default;

            return ConvertNode(rootVal);
        }
        catch
        {
            return default;
        }
    }

    private static SNode ConvertNode(object tsNode)
    {
        var t = tsNode.GetType();
        var kind = t.GetProperty("Kind")?.GetValue(tsNode)?.ToString() ?? "";
        var startPos = GetPoint(t.GetProperty("StartPosition")?.GetValue(tsNode));
        var endPos = GetPoint(t.GetProperty("EndPosition")?.GetValue(tsNode));
        var text = "";
        try { var textProp = t.GetProperty("Text"); if (textProp != null) text = textProp.GetValue(tsNode)?.ToString() ?? ""; } catch { }

        var children = new List<SNode>();
        try
        {
            var countProp = t.GetProperty("NamedChildCount");
            var childMethod = t.GetMethod("NamedChild");
            if (countProp != null && childMethod != null)
            {
                var count = (int)(countProp.GetValue(tsNode) ?? 0);
                for (var i = 0; i < count; i++)
                {
                    var child = childMethod.Invoke(tsNode, new object[] { i });
                    if (child != null) children.Add(ConvertNode(child));
                }
            }
        }
        catch { }

        return new SNode(kind, startPos.line, startPos.col, endPos.line, endPos.col,
            text.Length > 500 ? text[..500] : text, children);

        static (int line, int col) GetPoint(object? pointObj)
        {
            if (pointObj == null) return (0, 0);
            var pt = pointObj.GetType();
            var row = (int)(pt.GetProperty("Row")?.GetValue(pointObj) ?? 0);
            var col = (int)(pt.GetProperty("Column")?.GetValue(pointObj) ?? 0);
            return (row + 1, col + 1);
        }
    }

    private static List<AstFunction> ExtractFunctionsFromTree(SNode root, CodeLanguage lang)
    {
        var functions = new List<AstFunction>();
        var funcKinds = lang switch
        {
            CodeLanguage.Python => new[] { "function_definition" },
            CodeLanguage.JavaScript => new[] { "function_declaration", "method_definition", "arrow_function" },
            CodeLanguage.TypeScript => new[] { "function_declaration", "method_definition", "arrow_function" },
            CodeLanguage.Go => new[] { "function_declaration", "method_declaration" },
            CodeLanguage.Rust => new[] { "function_item" },
            CodeLanguage.Java => new[] { "method_declaration", "constructor_declaration" },
            _ => new[] { "function_definition", "function_declaration", "method_declaration" },
        };

        foreach (var node in FindNodes(root, funcKinds))
        {
            var name = FindChildText(node, lang switch
            {
                CodeLanguage.Python => new[] { "identifier" },
                CodeLanguage.Go => new[] { "identifier" },
                CodeLanguage.Rust => new[] { "identifier" },
                _ => new[] { "identifier", "name", "property_identifier" },
            });

            if (string.IsNullOrEmpty(name)) continue;

            var paramsList = FindChildNode(node, new[] { "parameters", "formal_parameters", "parameter_list" });
            var paramCount = paramsList.Children.Count(c => c.Kind == "identifier" || c.Kind == "parameter");

            functions.Add(new AstFunction
            {
                Name = name,
                Line = node.StartLine,
                EndLine = node.EndLine,
                Column = node.StartCol,
                ReturnType = FindChildText(node, new[] { "type_identifier", "predefined_type", "return_type" }) ?? "",
                Parameters = FindChildrenNames(paramsList, new[] { "identifier", "parameter" }),
                ParentClass = FindParentName(root, node.StartLine, lang),
                Complexity = 1.0,
            });
        }

        return functions;
    }

    private static List<AstClass> ExtractClassesFromTree(SNode root, CodeLanguage lang)
    {
        var classes = new List<AstClass>();
        var classKinds = lang switch
        {
            CodeLanguage.Python => new[] { "class_definition" },
            CodeLanguage.JavaScript => new[] { "class_declaration" },
            CodeLanguage.TypeScript => new[] { "class_declaration", "interface_declaration", "type_alias_declaration" },
            CodeLanguage.Go => new[] { "type_declaration", "type_spec" },
            CodeLanguage.Rust => new[] { "struct_item", "enum_item", "trait_item", "impl_item" },
            CodeLanguage.Java => new[] { "class_declaration", "interface_declaration", "enum_declaration" },
            _ => new[] { "class_definition", "class_declaration" },
        };

        foreach (var node in FindNodes(root, classKinds))
        {
            var name = FindChildText(node, new[] { "identifier", "name", "type_identifier" });
            if (string.IsNullOrEmpty(name)) continue;

            var kind = node.Kind switch
            {
                "interface_declaration" => "interface",
                "enum_declaration" or "enum_item" => "enum",
                "struct_item" => "struct",
                "trait_item" => "trait",
                "impl_item" => "impl",
                "type_alias_declaration" => "type",
                _ => "class",
            };

            classes.Add(new AstClass
            {
                Name = name,
                Line = node.StartLine,
                EndLine = node.EndLine,
                Column = node.StartCol,
                Kind = kind,
                MethodCount = 0,
            });
        }

        return classes;
    }

    private static List<AstImport> ExtractImportsFromTree(SNode root, CodeLanguage lang)
    {
        var imports = new List<AstImport>();
        var importKinds = lang switch
        {
            CodeLanguage.Python => new[] { "import_statement", "import_from_statement" },
            CodeLanguage.JavaScript => new[] { "import_statement" },
            CodeLanguage.TypeScript => new[] { "import_statement" },
            CodeLanguage.Go => new[] { "import_declaration", "import_spec" },
            CodeLanguage.Rust => new[] { "use_declaration" },
            CodeLanguage.Java => new[] { "import_declaration" },
            _ => new[] { "import_statement", "import_declaration", "use_declaration" },
        };

        foreach (var node in FindNodes(root, importKinds))
        {
            var module = FindModuleName(node, lang);
            if (string.IsNullOrEmpty(module)) module = node.Text.Trim();

            imports.Add(new AstImport
            {
                Module = module,
                Line = node.StartLine,
                Column = node.StartCol,
                ImportKind = node.Kind switch
                {
                    "import_from_statement" => "from-import",
                    "use_declaration" => "use",
                    _ => "import",
                },
            });
        }

        return imports;
    }

    private static string? FindModuleName(SNode node, CodeLanguage lang)
    {
        if (lang == CodeLanguage.Python)
        {
            var dotted = FindChildNode(node, new[] { "dotted_name" });
            if (dotted.Text.Length > 0) return dotted.Text.Trim();
        }

        var strChild = node.Children.FirstOrDefault(c =>
            c.Kind is "string" or "string_literal" or "scoped_identifier" or "identifier");
        var text = strChild.Text.Trim().Trim('"', '\'', '`');
        if (!string.IsNullOrEmpty(text)) return text;

        return lang switch
        {
            CodeLanguage.Rust => node.Text["use ".Length..].TrimEnd(';'),
            CodeLanguage.Go => node.Text.Trim('"', '\''),
            _ => null,
        };
    }

    private static List<SNode> FindNodes(SNode node, string[] kinds)
    {
        var results = new List<SNode>();
        if (kinds.Contains(node.Kind)) results.Add(node);
        foreach (var child in node.Children)
            results.AddRange(FindNodes(child, kinds));
        return results;
    }

    private static SNode FindChildNode(SNode node, string[] kinds)
    {
        foreach (var child in node.Children)
        {
            if (kinds.Contains(child.Kind)) return child;
            var found = FindChildNode(child, kinds);
            if (found.Kind.Length > 0) return found;
        }
        return default;
    }

    private static string? FindChildText(SNode node, string[] kinds)
    {
        foreach (var child in node.Children)
        {
            if (kinds.Contains(child.Kind) && !string.IsNullOrWhiteSpace(child.Text))
                return child.Text.Trim();
        }
        foreach (var child in node.Children)
        {
            var found = FindChildText(child, kinds);
            if (found != null) return found;
        }
        return null;
    }

    private static List<string> FindChildrenNames(SNode node, string[] kinds)
    {
        var names = new List<string>();
        foreach (var child in node.Children)
        {
            if (kinds.Contains(child.Kind) && !string.IsNullOrWhiteSpace(child.Text))
                names.Add(child.Text.Trim());
            names.AddRange(FindChildrenNames(child, kinds));
        }
        return names;
    }

    private static string? FindParentName(SNode root, int line, CodeLanguage lang)
    {
        var classKinds = lang switch
        {
            CodeLanguage.Python => new[] { "class_definition" },
            CodeLanguage.TypeScript => new[] { "class_declaration", "interface_declaration" },
            _ => new[] { "class_definition", "class_declaration" },
        };

        var classes = FindNodes(root, classKinds);
        SNode best = default;
        foreach (var cls in classes)
        {
            if (cls.StartLine <= line && cls.EndLine >= line)
            {
                if (best.Kind.Length == 0 || cls.StartLine > best.StartLine)
                    best = cls;
            }
        }

        if (best.Kind.Length == 0) return null;
        return FindChildText(best, new[] { "identifier", "name", "type_identifier" });
    }

    private static void FallbackParse(string code, CodeParseResult result)
    {
        var info = LanguageRegistry.Get(result.Language);
        result.Functions = FallbackExtractFunctions(code, info);
        result.Classes = FallbackExtractClasses(code, info);
        result.Imports = FallbackExtractImports(code, info);
    }

    private static List<AstFunction> FallbackExtractFunctions(string code, LanguageInfo info)
    {
        var functions = new List<AstFunction>();
        var lines = code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            string? name = null;

            switch (info.Language)
            {
                case CodeLanguage.Python:
                    var pyMatch = Regex.Match(line, @"^\s*def\s+(\w+)\s*\(([^)]*)\)");
                    if (pyMatch.Success) name = pyMatch.Groups[1].Value;
                    break;
                case CodeLanguage.JavaScript:
                case CodeLanguage.TypeScript:
                    var jsFn = Regex.Match(line, @"(?:function\s+(\w+)|(?:const|let|var)\s+(\w+)\s*=\s*(?:async\s*)?\(|(\w+)\s*\([^)]*\)\s*\{)");
                    if (jsFn.Success) name = jsFn.Groups[1].Success ? jsFn.Groups[1].Value : jsFn.Groups[2].Success ? jsFn.Groups[2].Value : jsFn.Groups[3].Value;
                    break;
                case CodeLanguage.Go:
                    var goFn = Regex.Match(line, @"^\s*func\s+(?:\(\w+\s+\*?\w+\)\s+)?(\w+)\s*\(");
                    if (goFn.Success) name = goFn.Groups[1].Value;
                    break;
                case CodeLanguage.Rust:
                    var rsFn = Regex.Match(line, @"^\s*(?:pub\s+)?fn\s+(\w+)\s*[<(]");
                    if (rsFn.Success) name = rsFn.Groups[1].Value;
                    break;
                case CodeLanguage.Java:
                    var javaFn = Regex.Match(line, @"(?:public|private|protected|static|\s)+(\w+)\s+(\w+)\s*\(");
                    if (javaFn.Success && !line.Contains("class ")) name = javaFn.Groups[2].Value;
                    break;
            }

            if (name != null)
                functions.Add(new AstFunction { Name = name, Line = i + 1, EndLine = i + 5, Column = 1 });
        }
        return functions;
    }

    private static List<AstClass> FallbackExtractClasses(string code, LanguageInfo info)
    {
        var classes = new List<AstClass>();
        var lines = code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            string? name = null;

            switch (info.Language)
            {
                case CodeLanguage.Python:
                    var pyCls = Regex.Match(line, @"^\s*class\s+(\w+)");
                    if (pyCls.Success) name = pyCls.Groups[1].Value;
                    break;
                case CodeLanguage.JavaScript:
                    var jsCls = Regex.Match(line, @"class\s+(\w+)");
                    if (jsCls.Success) name = jsCls.Groups[1].Value;
                    break;
                case CodeLanguage.TypeScript:
                    var tsCls = Regex.Match(line, @"(?:class|interface)\s+(\w+)");
                    if (tsCls.Success) name = tsCls.Groups[1].Value;
                    break;
                case CodeLanguage.Go:
                    var goType = Regex.Match(line, @"^\s*type\s+(\w+)\s+struct");
                    if (goType.Success) name = goType.Groups[1].Value;
                    break;
                case CodeLanguage.Rust:
                    var rsType = Regex.Match(line, @"^\s*(?:pub\s+)?(?:struct|enum|trait)\s+(\w+)");
                    if (rsType.Success) name = rsType.Groups[1].Value;
                    break;
                case CodeLanguage.Java:
                    var javaCls = Regex.Match(line, @"(?:public\s+)?(?:class|interface)\s+(\w+)");
                    if (javaCls.Success) name = javaCls.Groups[1].Value;
                    break;
            }

            if (name != null)
                classes.Add(new AstClass { Name = name, Line = i + 1, EndLine = i + 10, Column = 1 });
        }
        return classes;
    }

    private static List<AstImport> FallbackExtractImports(string code, LanguageInfo info)
    {
        var imports = new List<AstImport>();
        var lines = code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            string? module = null;

            switch (info.Language)
            {
                case CodeLanguage.Python:
                    var pyImp = Regex.Match(line, @"^(?:import\s+(\w+)|from\s+(\S+)\s+import)");
                    if (pyImp.Success) module = pyImp.Groups[1].Success ? pyImp.Groups[1].Value : pyImp.Groups[2].Value;
                    break;
                case CodeLanguage.JavaScript:
                case CodeLanguage.TypeScript:
                    var jsImp = Regex.Match(line, @"(?:import\s+.*?\s+from\s+['""](.+?)['""]|require\s*\(['""](.+?)['""]\))");
                    if (jsImp.Success) module = jsImp.Groups[1].Success ? jsImp.Groups[1].Value : jsImp.Groups[2].Value;
                    break;
                case CodeLanguage.Go:
                    var goImp = Regex.Match(line, "\"([^\"]+)\"");
                    if (goImp.Success) module = goImp.Groups[1].Value;
                    break;
                case CodeLanguage.Rust:
                    var rsUse = Regex.Match(line, @"^use\s+([\w:]+)");
                    if (rsUse.Success) module = rsUse.Groups[1].Value;
                    break;
                case CodeLanguage.Java:
                    var javaImp = Regex.Match(line, @"^import\s+([\w.]+)");
                    if (javaImp.Success) module = javaImp.Groups[1].Value;
                    break;
            }

            if (module != null)
                imports.Add(new AstImport { Module = module, Line = i + 1, Column = 1 });
        }
        return imports;
    }

    private static int CountCodeLines(string[] lines, CodeLanguage lang)
    {
        var info = LanguageRegistry.Get(lang);
        return lines.Count(l =>
        {
            var t = l.Trim();
            return !string.IsNullOrEmpty(t) && !t.StartsWith(info.SingleLineComment) &&
                   !IsMultiLineStart(t, info);
        });
    }

    private static int CountCommentLines(string[] lines, CodeLanguage lang)
    {
        var info = LanguageRegistry.Get(lang);
        return lines.Count(l =>
        {
            var t = l.Trim();
            return t.StartsWith(info.SingleLineComment) || IsMultiLineStart(t, info);
        });
    }

    private static int CountBlankLines(string[] lines) => lines.Count(string.IsNullOrWhiteSpace);

    private static bool IsMultiLineStart(string line, LanguageInfo info)
    {
        return !string.IsNullOrEmpty(info.MultiLineCommentStart) && line.StartsWith(info.MultiLineCommentStart);
    }
}
