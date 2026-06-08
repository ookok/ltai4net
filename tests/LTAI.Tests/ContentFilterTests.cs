// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  ContentFilterTests — three-layer knowledge graph guard tests
// ═══════════════════════════════════════════════════════════════

using Xunit;
using LTAI.Agent.Indexing;

namespace LTAI.Tests;

public class ContentFilterLayer1Path
{
    [Fact]
    public void ScreenPath_LogDir_Blocked()
        => Assert.Equal(FilterVerdict.Blocked_Path, ContentFilter.ScreenPath("logs/app.log"));

    [Fact]
    public void ScreenPath_NodeModules_Blocked()
        => Assert.Equal(FilterVerdict.Blocked_Path, ContentFilter.ScreenPath("node_modules/express/index.js"));

    [Fact]
    public void ScreenPath_SourceCode_Allowed()
        => Assert.Equal(FilterVerdict.Allowed, ContentFilter.ScreenPath("src/Program.cs"));

    [Fact]
    public void ScreenPath_Markdown_Allowed()
        => Assert.Equal(FilterVerdict.Allowed, ContentFilter.ScreenPath("docs/readme.md"));

    [Fact]
    public void ScreenPath_BinaryExe_Blocked()
        => Assert.Equal(FilterVerdict.Blocked_Path, ContentFilter.ScreenPath("bin/tool.exe"));

    [Fact]
    public void ScreenPath_NoExtension_Blocked()
        => Assert.Equal(FilterVerdict.Blocked_Path, ContentFilter.ScreenPath("Makefile"));

    [Fact]
    public void ScreenPath_BuildDir_Blocked()
        => Assert.Equal(FilterVerdict.Blocked_Path, ContentFilter.ScreenPath("build/output.o"));

    [Fact]
    public void ScreenPath_DotVsDir_Blocked()
        => Assert.Equal(FilterVerdict.Blocked_Path, ContentFilter.ScreenPath(".vs/config.vs"));

    [Fact]
    public void ScreenPath_SandboxDir_Blocked()
        => Assert.Equal(FilterVerdict.Blocked_Path, ContentFilter.ScreenPath(".sandbox/scratch.txt"));

    [Fact]
    public void ScreenPath_LogExtension_Allowed()
        => Assert.Equal(FilterVerdict.Allowed, ContentFilter.ScreenPath("app.log"));
    // NOTE: .log is not in BinaryExtensions. Layer 2 content screening catches it.
    // Layer 1 allows the path; Layer 2 blocks the content.
}

public class ContentFilterLayer2Content
{
    [Fact]
    public void ScreenContent_LogFile_Blocked()
    {
        var log = "2026-06-01 10:00:00 INFO  Starting service...\n"
                + "2026-06-01 10:00:01 WARN  Resource low\n"
                + "2026-06-01 10:00:02 ERROR Connection failed\n"
                + "2026-06-01 10:00:03 DEBUG Retrying...\n"
                + "2026-06-01 10:00:04 INFO  Retry 2\n"
                + "2026-06-01 10:00:05 WARN  Timeout\n"
                + "2026-06-01 10:00:06 INFO  Shutting down\n";
        Assert.Equal(FilterVerdict.Blocked_LogNoise, ContentFilter.ScreenContent(log, "test.log"));
    }

    [Fact]
    public void ScreenContent_HighDigitRatio_Blocked()
    {
        var noisy = string.Join(" ", Enumerable.Range(0, 20).Select(i => $"2026-06-{i:D2} 10:00:0{i} token={i * 100}"));
        Assert.Equal(FilterVerdict.Blocked_LogNoise, ContentFilter.ScreenContent(noisy, "timestamps.txt"));
    }

    [Fact]
    public void ScreenContent_LogHeavy_Blocked()
    {
        // 80% lines (8/10) have log prefixes → exceeds MaxLogLineRatio of 60%
        var log = "";
        for (int i = 0; i < 10; i++)
            log += $"INFO  Processing item {i}\n";
        var result = ContentFilter.ScreenContent(log, "output.log");
        Assert.True(result == FilterVerdict.Blocked_LogNoise || result == FilterVerdict.Blocked_LowQuality,
            $"Expected LogNoise or LowQuality, got {result}");
    }

    [Fact]
    public void ScreenContent_MixedStackAndLog_NotBlocked()
    {
        // Only 50% (3/6) lines have log prefixes — below 60% threshold
        var stack = "ERROR NullReferenceException\n"
                  + "  at MyClass.Method() in /src/MyClass.cs:line 100\n"
                  + "WARN  Resource leak\n"
                  + "ERROR OutOfMemory\n"
                  + "DEBUG Stack trace:\n"
                  + "  at System.ThrowHelper.Throw()\n";
        Assert.Equal(FilterVerdict.Allowed, ContentFilter.ScreenContent(stack, "error.txt"));
    }

    [Fact]
    public void ScreenContent_PureStackTrace_Allowed_OnlyL2Check()
    {
        // Pure stack trace (no INFO/WARN/ERROR/DEBUG prefixes) doesn't trigger log detection
        var stack = "at System.ThrowHelper.Throw() in /src/Throw.cs:line 42\n"
                  + "at MyClass.Method() in /src/MyClass.cs:line 100\n";
        Assert.Equal(FilterVerdict.Allowed, ContentFilter.ScreenContent(stack, "error.txt"));
    }

    [Fact]
    public void ScreenContent_HighSymbolRatio_Blocked()
    {
        var symbols = ">>>---===###@@@!!!~~~^^^***((())){{}}[[|]];;;'''\"\"\"<<<>>>";
        Assert.Equal(FilterVerdict.Blocked_LogNoise, ContentFilter.ScreenContent(symbols, "symbols.txt"));
    }

    [Fact]
    public void ScreenContent_ShortContent_Blocked()
    {
        Assert.Equal(FilterVerdict.Blocked_Size, ContentFilter.ScreenContent("hi", "short.txt"));
    }

    [Fact]
    public void ScreenContent_Empty_Blocked()
    {
        Assert.Equal(FilterVerdict.Blocked_Size, ContentFilter.ScreenContent("", "empty.txt"));
    }

    [Fact]
    public void ScreenContent_GoodCode_Allowed()
    {
        var code = "using System;\n\n"
                 + "namespace MyApp {\n"
                 + "    public class HelloWorld {\n"
                 + "        public static void Main(string[] args) {\n"
                 + "            Console.WriteLine(\"Hello, World!\");\n"
                 + "        }\n"
                 + "    }\n"
                 + "}\n";
        Assert.Equal(FilterVerdict.Allowed, ContentFilter.ScreenContent(code, "hello.cs"));
    }

    [Fact]
    public void ScreenContent_GoodMarkdown_Allowed()
    {
        var md = "# Project Documentation\n\n"
               + "This project implements a **knowledge graph** with FTS5 search.\n\n"
               + "## Features\n\n"
               + "- Entity extraction\n- Relation mapping\n- Semantic search\n\n"
               + "See [getting started](docs/start.md) for more.\n";
        Assert.Equal(FilterVerdict.Allowed, ContentFilter.ScreenContent(md, "readme.md"));
    }

    [Fact]
    public void ScreenContent_JsonConfig_BlockedBySymbolRatio()
    {
        // JSON has high symbol ratio (:, \", {, }, [, ], ,) which exceeds MaxSymbolRatio
        var json = "{\n  \"name\": \"LTAI\",\n  \"version\": \"1.0\",\n  \"dependencies\": [\n"
                 + "    \"Microsoft.Extensions.AI\"\n  ]\n}\n";
        Assert.Equal(FilterVerdict.Blocked_LogNoise, ContentFilter.ScreenContent(json, "appsettings.json"));
    }

    [Fact]
    public void ScreenContent_AverageLongLines_Blocked()
    {
        var content = string.Join("\n", Enumerable.Range(0, 10).Select(_ =>
            new string('x', 600) + new string('0', 200)));
        Assert.Equal(FilterVerdict.Blocked_LogNoise, ContentFilter.ScreenContent(content, "minified.txt"));
    }

    [Fact]
    public void ScreenContent_RepetitiveBoilerplate_Blocked()
    {
        var boilerplate = string.Join("\n", Enumerable.Repeat(
            "The company provides innovative solutions for enterprise customers. "
            + "We leverage cutting-edge technology to deliver value. "
            + "Our team is committed to excellence and customer satisfaction.",
            30));
        Assert.Equal(FilterVerdict.Blocked_LowQuality, ContentFilter.ScreenContent(boilerplate, "boilerplate.txt"));
    }
}

public class ContentFilterLayer3Quality
{
    [Fact]
    public void ScreenExtraction_GoodConcept_Allowed()
    {
        Assert.Equal(FilterVerdict.Allowed,
            ContentFilter.ScreenExtraction("VectorDatabase", "A database optimized for storing and searching vector embeddings"));
    }

    [Fact]
    public void ScreenExtraction_ShortConcept_Blocked()
    {
        Assert.Equal(FilterVerdict.Blocked_LowQuality,
            ContentFilter.ScreenExtraction("x", "short"));
    }

    [Fact]
    public void ScreenExtraction_ErrorLikeConcept_Allowed()
    {
        // "Exception" + "unexpected error": error penalty -0.3, length bonus +0.1 → 0.3 ≥ 0.3 threshold
        var result = ContentFilter.ScreenExtraction("Exception", "unexpected error");
        Assert.Equal(FilterVerdict.Allowed, result);
    }

    [Fact]
    public void ScreenExtraction_GenericConcept_Allowed()
    {
        // "the thing" + "general stuff" — generic penalty -0.2, length bonus +0.1 = 0.4 > 0.3
        Assert.Equal(FilterVerdict.Allowed,
            ContentFilter.ScreenExtraction("the thing", "this is a note about general stuff"));
    }

    [Fact]
    public void ScreenExtraction_EmptySummary_Blocked()
    {
        Assert.Equal(FilterVerdict.Blocked_LowQuality,
            ContentFilter.ScreenExtraction("Concept", ""));
    }

    [Fact]
    public void ScreenExtraction_CodePattern_Boosted()
    {
        Assert.Equal(FilterVerdict.Allowed,
            ContentFilter.ScreenExtraction("KgStore", "SQLite-backed knowledge graph store with FTS5 and vector search in LTAI.Agent.Vector"));
    }
}

public class ContentFilterIntegration
{
    [Fact]
    public void LogFile_FullPipeline_BlockedAtAllLayers()
    {
        // Full pipeline: .log extension + log content = blocked by L1+L2
        var pathVerdict = ContentFilter.ScreenPath("logs/app.log");
        var contentVerdict = ContentFilter.ScreenContent(
            "2026-06-01 10:00:00 ERROR OutOfMemoryException\n", "app.log");

        Assert.Equal(FilterVerdict.Blocked_Path, pathVerdict);
        Assert.NotEqual(FilterVerdict.Allowed, contentVerdict);
    }

    [Fact]
    public void SourceCode_FullPipeline_Allowed()
    {
        var code = "public sealed class TestClass {\n    public int Add(int a, int b) => a + b;\n}\n";
        var pathVerdict = ContentFilter.ScreenPath("src/TestClass.cs");
        var contentVerdict = ContentFilter.ScreenContent(code, "TestClass.cs");

        Assert.Equal(FilterVerdict.Allowed, pathVerdict);
        Assert.Equal(FilterVerdict.Allowed, contentVerdict);
    }

    [Fact]
    public void IsAllowedExtension_Log_ReturnsFalse()
    {
        Assert.False(ContentFilter.IsAllowedExtension(".log"));
    }

    [Fact]
    public void IsAllowedExtension_Cs_ReturnsTrue()
    {
        Assert.True(ContentFilter.IsAllowedExtension(".cs"));
    }

    [Fact]
    public void IsSkippedDirectory_Logs_ReturnsTrue()
    {
        Assert.True(ContentFilter.IsSkippedDirectory("logs"));
    }

    [Fact]
    public void IsSkippedDirectory_Src_ReturnsFalse()
    {
        Assert.False(ContentFilter.IsSkippedDirectory("src"));
    }
}
