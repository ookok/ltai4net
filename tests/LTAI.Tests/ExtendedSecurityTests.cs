using Xunit;
using LTAI.Core;
using LTAI.Core.Safety;

namespace LTAI.Tests;

public sealed class ExtendedSecurityTests
{
    // ═══════════════════════════════════════════════
    //  Path Traversal
    // ═══════════════════════════════════════════════

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\Windows\\System32\\config\\SAM")]
    [InlineData("....//....//etc/passwd")]
    [InlineData("%2e%2e%2fetc%2fpasswd")]
    [InlineData("..;/etc/passwd")]
    [InlineData("....\\/etc/passwd")]
    public void SafeResolvePath_BlocksTraversal(string path)
    {
        var result = PathUtils.SafeResolvePath(Environment.CurrentDirectory, path);
        Assert.Null(result);
    }

    [Theory]
    [InlineData("test.txt")]
    [InlineData("subdir/file.txt")]
    [InlineData("src/LTAI.Core/Config.cs")]
    [InlineData(".livingtree/config.yaml")]
    public void SafeResolvePath_AllowsValidPaths(string path)
    {
        var result = PathUtils.SafeResolvePath(Environment.CurrentDirectory, path);
        Assert.NotNull(result);
    }

    [Fact]
    public void SafeResolvePath_EmptyAndNull_ReturnsNull()
    {
        Assert.Null(PathUtils.SafeResolvePath(Environment.CurrentDirectory, ""));
        Assert.Null(PathUtils.SafeResolvePath(Environment.CurrentDirectory, "   "));
        Assert.Null(PathUtils.SafeResolvePath(Environment.CurrentDirectory, null!));
    }

    [Fact]
    public void RequireSafePath_ValidPath_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            PathUtils.RequireSafePath(Environment.CurrentDirectory, "test.txt"));
        Assert.Null(ex);
    }

    [Fact]
    public void RequireSafePath_Traversal_ThrowsUnauthorized()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            PathUtils.RequireSafePath(Environment.CurrentDirectory,
                $"..{Path.DirectorySeparatorChar}..{Path.DirectorySeparatorChar}etc"));
    }

    // ═══════════════════════════════════════════════
    //  SQL Injection Detection
    // ═══════════════════════════════════════════════

    [Theory]
    [InlineData("DROP TABLE users", false)]
    [InlineData("DELETE FROM accounts WHERE 1=1", false)]
    [InlineData("TRUNCATE TABLE logs", false)]
    [InlineData("EXEC xp_cmdshell 'dir'", false)]
    [InlineData("SELECT * FROM users WHERE name = 'admin'", true)]
    [InlineData("How do I query a database?", true)]
    public void SafetyRules_DetectsSqlInjection(string input, bool expectedSafe)
    {
        Assert.Equal(expectedSafe, SafetyRules.IsSafeByRules(input));
    }

    // ═══════════════════════════════════════════════
    //  API Key / Credential Leak Detection
    // ═══════════════════════════════════════════════

    [Theory]
    [InlineData("api_key=sk-1234567890abcdef1234567890abcdef", false)]
    [InlineData("token: a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6q7r8s9t0", false)]
    [InlineData("secret a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5", false)]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", false)]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----", false)]
    [InlineData("-----BEGIN EC PRIVATE KEY-----", false)]
    [InlineData("const password = 'normal'", true)]
    public void SafetyRules_DetectsCredentials(string input, bool expectedSafe)
    {
        Assert.Equal(expectedSafe, SafetyRules.IsSafeByRules(input));
    }

    // ═══════════════════════════════════════════════
    //  XSS Detection
    // ═══════════════════════════════════════════════

    [Theory]
    [InlineData("<script>alert('xss')</script>", false)]
    [InlineData("<img src=x onerror=alert(1)>", false)]
    [InlineData("<svg onload=alert(1)>", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("<p>Hello World</p>", true)]
    [InlineData("This is a normal text", true)]
    public void SafetyRules_DetectsXss(string input, bool expectedSafe)
    {
        Assert.Equal(expectedSafe, SafetyRules.IsSafeByRules(input));
    }

    // ═══════════════════════════════════════════════
    //  PII Detection (Credit Cards, Phone Numbers)
    // ═══════════════════════════════════════════════

    [Theory]
    [InlineData("4111 1111 1111 1111", false)]
    [InlineData("4111-1111-1111-1111", false)]
    [InlineData("5500 0000 0000 0004", false)]
    [InlineData("+86 138 0013 8000", false)]
    [InlineData("1-555-123-4567", false)]
    [InlineData("My number is 5", true)]
    public void SafetyRules_DetectsPii(string input, bool expectedSafe)
    {
        Assert.Equal(expectedSafe, SafetyRules.IsSafeByRules(input));
    }

    // ═══════════════════════════════════════════════
    //  Edge cases
    // ═══════════════════════════════════════════════

    [Fact]
    public void SafetyRules_VeryLongInput_DoesNotCrash()
    {
        var longText = new string('A', 200_000);
        var ex = Record.Exception(() => SafetyRules.IsSafeByRules(longText));
        Assert.Null(ex);
    }

    [Fact]
    public void SafetyRules_OnlySymbols_DoesNotCrash()
    {
        var ex = Record.Exception(() => SafetyRules.IsSafeByRules("!@#$%^&*()_+-={}[]|:;'<>?,./"));
        Assert.Null(ex);
    }

    [Fact]
    public void SafetyRules_UnicodeText_PassesIfSafe()
    {
        var safe = SafetyRules.IsSafeByRules("こんにちは世界");
        Assert.True(safe);
    }

    [Fact]
    public void PathUtils_CheckFileSize_TooLarge_ReturnsError()
    {
        var path = Path.Combine(Path.GetTempPath(), "ltai-large-test.bin");
        try
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            fs.SetLength(20 * 1024 * 1024); // 20MB
            fs.Close();
            var result = PathUtils.CheckFileSize(path, maxBytes: 10 * 1024 * 1024);
            Assert.NotNull(result);
            Assert.Contains("File too large", result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
