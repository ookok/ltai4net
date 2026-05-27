using LTAI.Core.Governors;
using Xunit;

namespace LTAI.Tests;

public sealed class RuleLoaderTests
{
    // ========================================================================
    // RuleLoader: Parse .md rule files from rules/ directory
    // ========================================================================

    [Fact]
    public async Task LoadAllAsync_RulesDirectoryFound_LoadsAllFiles()
    {
        var rulesDir = Path.Combine(
            Environment.CurrentDirectory.Contains("Debug")
                ? Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "..", "..", "..", ".."))
                : Environment.CurrentDirectory,
            "rules");

        if (!Directory.Exists(rulesDir))
        {
            Assert.True(false, $"Rules directory not found: {rulesDir}. Run from project root.");
            return;
        }

        var loader = new RuleLoader(rulesDir);
        var rules = await loader.LoadAllAsync();

        Assert.NotEmpty(rules);
        Assert.True(rules.Count >= 5, $"Expected at least 5 rules, got {rules.Count}");

        foreach (var rule in rules)
        {
            Assert.NotEmpty(rule.Domain);
            Assert.True(rule.Keywords.Length > 0 || rule.Patterns.Length > 0,
                $"Rule '{rule.Domain}' has no keywords or patterns");
            Assert.NotEmpty(rule.SourceFile);
        }
    }

    [Fact]
    public async Task LoadAllAsync_NonExistentDir_ReturnsEmpty()
    {
        var loader = new RuleLoader(Path.Combine(Path.GetTempPath(), $"ltai_no_rules_{Guid.NewGuid():N}"));
        var rules = await loader.LoadAllAsync();

        Assert.NotNull(rules);
        Assert.Empty(rules);
    }

    [Fact]
    public async Task LoadAsync_SingleRuleFile_ParsesCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ltai_rules_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var ruleContent = @"# rule: chat
domain: chat
quality: 0.6
speed: 0.8
cost: 0.0
description: Casual conversation intent
keywords: hello, hi, good morning, how are you, what's up
patterns: ^(hi|hello|hey)[\s\.,!?]*$
patterns: ^how are you
";

            await File.WriteAllTextAsync(Path.Combine(tempDir, "chat.md"), ruleContent);

            var loader = new RuleLoader(tempDir);
            var parsed = await loader.LoadAsync(Path.Combine(tempDir, "chat.md"));

            Assert.NotNull(parsed);
            Assert.Equal("chat", parsed.Domain);
            Assert.Equal(0.6f, parsed.Quality);
            Assert.Equal(0.8f, parsed.Speed);
            Assert.Equal(0f, parsed.Cost);
            Assert.NotEmpty(parsed.Keywords);
            Assert.Contains("hello", parsed.Keywords);
            Assert.NotEmpty(parsed.Patterns);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_RuleParsing_HandlesMultipleKeywords()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ltai_rules_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var content = @"# rule: code
domain: code
quality: 0.95
speed: 0.1
cost: 1.0
description: Programming and code generation
keywords: code, programming, function, class, debug, compile, refactor, algorithm, implement, write
patterns: ^(write|implement)\s+(a|the)\s+(function|class|method|algorithm)
";

            await File.WriteAllTextAsync(Path.Combine(tempDir, "code.md"), content);

            var loader = new RuleLoader(tempDir);
            var parsed = await loader.LoadAsync(Path.Combine(tempDir, "code.md"));

            Assert.NotNull(parsed);
            Assert.Equal("code", parsed.Domain);
            Assert.True(parsed.Keywords.Length >= 8, $"Expected >=8 keywords, got {parsed.Keywords.Length}");
            Assert.Contains("function", parsed.Keywords);
            Assert.Contains("algorithm", parsed.Keywords);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ========================================================================
    // IntentRule matching tests
    // ========================================================================

    [Fact]
    public void IntentRule_KeywordMatch_Works()
    {
        var rule = new IntentRule
        {
            Domain = "code",
            Keywords = new[] { "function", "class", "algorithm" }
        };

        Assert.Contains("function", rule.Keywords);
        Assert.Equal("code", rule.Domain);
    }

    [Fact]
    public void IntentRule_PatternMatch_Works()
    {
        var pattern = new System.Text.RegularExpressions.Regex(@"^(write|implement)\s+(a|the)", System.Text.RegularExpressions.RegexOptions.Compiled);

        Assert.True(pattern.IsMatch("write a function"));
        Assert.True(pattern.IsMatch("implement the algorithm"));
        Assert.False(pattern.IsMatch("hello world"));
    }

    [Fact]
    public async Task LoadAllAsync_RealRules_HaveValidDomains()
    {
        var rulesDir = Path.Combine(
            Environment.CurrentDirectory.Contains("Debug")
                ? Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "..", "..", "..", ".."))
                : Environment.CurrentDirectory,
            "rules");

        if (!Directory.Exists(rulesDir))
            return;

        var loader = new RuleLoader(rulesDir);
        var rules = await loader.LoadAllAsync();

        var domains = new HashSet<string> { "code", "math", "reasoning", "eia", "chat", "translation", "summarization", "reflex", "command" };

        foreach (var rule in rules)
        {
            Assert.True(domains.Contains(rule.Domain),
                $"Unknown domain '{rule.Domain}' in rule file {rule.SourceFile}");
        }
    }
}
