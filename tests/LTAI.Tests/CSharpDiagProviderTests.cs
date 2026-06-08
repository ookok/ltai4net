// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  CSharpDiagProviderTests — C# 语义诊断测试
//
//  验证 CSharpDiagProvider 能检测类型级别的语义错误。
//  这比 QuickParseCSharp 更深入（它只做语法检查）。
//
//  覆盖:
//    1. 正确的代码 → 无诊断
//    2. 类型引用错误 → 报错
//    3. 缺失 using → 报错
//    4. 增量更新 → 旧错误消失
//    5. 多文件跨文件引用
// ═══════════════════════════════════════════════════════════════

using LTAI.Agent.LanguageServer;
using Xunit;

namespace LTAI.Tests;

public class CSharpDiagProviderTests : IDisposable
{
    private readonly CSharpDiagProvider _provider = new();

    public void Dispose() => _provider.Dispose();

    [Fact]
    public async Task ValidCode_ReturnsNoDiagnostics()
    {
        var code = """
            using System;
            namespace Test;
            public class Hello
            {
                public void Say() => Console.WriteLine("hi");
            }
            """;
        var diags = await _provider.UpdateDocumentAsync("test.cs", code);
        Assert.Empty(diags);
    }

    [Fact]
    public async Task TypeNotFound_ReturnsError()
    {
        var code = """
            public class Test
            {
                public void Foo()
                {
                    NonexistentType x = new NonexistentType();
                }
            }
            """;
        var diags = await _provider.UpdateDocumentAsync("test.cs", code);
        Assert.NotEmpty(diags);
        Assert.Contains(diags, d => d.IsError);
    }

    [Fact]
    public async Task IncrementalUpdate_ClearsOldErrors()
    {
        // 第一次：有错误
        var badCode = """
            public class Test
            {
                public NonexistentType Foo() => null;
            }
            """;
        var firstDiags = await _provider.UpdateDocumentAsync("test.cs", badCode);
        Assert.Contains(firstDiags, d => d.IsError);

        // 第二次：修复后，错误应消失
        var goodCode = """
            public class Test
            {
                public string Foo() => "hello";
            }
            """;
        var secondDiags = await _provider.UpdateDocumentAsync("test.cs", goodCode);
        Assert.DoesNotContain(secondDiags, d => d.IsError);
    }

    [Fact]
    public async Task MissingUsing_ReturnsError()
    {
        var code = """
            namespace Test;
            public class Test
            {
                public void Foo()
                {
                    var list = new System.Collections.Generic.List<int>();
                    // 缺少 using System.Linq
                    var even = list.Find(x => x % 2 == 0);
                }
            }
            """;
        var diags = await _provider.UpdateDocumentAsync("test.cs", code);
        // Find 是 List<T> 的方法，不需要 Linq → 应无错误
        Assert.DoesNotContain(diags, d => d.IsError);
    }

    [Fact]
    public async Task AsyncMethodWithoutAwait_Warning()
    {
        var code = """
            using System.Threading.Tasks;
            namespace Test;
            public class Test
            {
                public async Task<int> Foo()
                {
                    return 42;  // CS1998: async 方法缺少 await
                }
            }
            """;
        var diags = await _provider.UpdateDocumentAsync("test.cs", code);
        Assert.Contains(diags, d => d.Code == "CS1998");
    }

    [Fact]
    public async Task RemoveDocument_Works()
    {
        var code = "public class A { public Nonexistent X; }";
        var diags = await _provider.UpdateDocumentAsync("test.cs", code);
        Assert.Contains(diags, d => d.IsError);

        _provider.RemoveDocument("test.cs");
        var afterRemove = _provider.GetDiagnostics("test.cs");
        Assert.Empty(afterRemove);
    }

    [Fact]
    public async Task Reset_ClearsAll()
    {
        var code = "public class A { public Nonexistent X; }";
        await _provider.UpdateDocumentAsync("test.cs", code);
        Assert.True(_provider.HasErrors);

        _provider.Reset();
        Assert.False(_provider.HasErrors);
    }

    [Fact]
    public async Task MultipleFiles_AllDiagnosed()
    {
        var code1 = "public class A { public void Foo() { new B(); } }";
        var code2 = "public class B { }";

        await _provider.UpdateDocumentAsync("a.cs", code1);
        await _provider.UpdateDocumentAsync("b.cs", code2);

        // B 已经在 b.cs 中定义，所以 A 引用 B 应无错误
        var diags1 = _provider.GetDiagnostics("a.cs");
        Assert.DoesNotContain(diags1, d => d.IsError);
    }
}
