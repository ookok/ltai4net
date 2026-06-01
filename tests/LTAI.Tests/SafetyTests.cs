using Xunit;
using LTAI.Core;
using LTAI.Core.Safety;

namespace LTAI.Tests;

public class SafetyRulesTests
{
    [Fact] public void SafeText_Passes() => Assert.True(SafetyRules.IsSafeByRules("Hello, how are you?"));
    [Fact] public void Empty_ReturnsTrue() => Assert.True(SafetyRules.IsSafeByRules(""));
    [Fact] public void Null_ReturnsTrue() => Assert.True(SafetyRules.IsSafeByRules(null!));
    [Fact] public void ApiKey_Blocked() => Assert.False(SafetyRules.IsSafeByRules("my api_key abc123def456ghi789jkl"));
    [Fact] public void CreditCard_Blocked() => Assert.False(SafetyRules.IsSafeByRules("4111 1111 1111 1111"));
    [Fact] public void CreditCardDashed_Blocked() => Assert.False(SafetyRules.IsSafeByRules("4111-1111-1111-1111"));
    [Fact] public void SqlInject_DropTable_Blocked() => Assert.False(SafetyRules.IsSafeByRules("DROP TABLE users"));
    [Fact] public void Xss_ScriptTag_Blocked() => Assert.False(SafetyRules.IsSafeByRules("<script>alert('xss')</script>"));
    [Fact] public void PhoneNumber_Blocked() => Assert.False(SafetyRules.IsSafeByRules("+86 138 0013 8000"));
    [Fact] public void PemKey_Blocked() => Assert.False(SafetyRules.IsSafeByRules("-----BEGIN RSA PRIVATE KEY-----"));
    [Fact] public void ApiKeyInContext_Blocked() => Assert.False(SafetyRules.IsSafeByRules("use secret a1b2c3d4e5f6g7h8 to connect"));
    [Fact] public void NormalUrl_Passes() => Assert.True(SafetyRules.IsSafeByRules("Check https://example.com for details"));
    [Fact] public void CodeSnippet_Passes() => Assert.True(SafetyRules.IsSafeByRules("int x = 42; // this is fine"));
}

public class PathUtilsTests
{
    [Fact]
    public void SafeResolvePath_NullInput_ReturnsNull()
    {
        var ws = Environment.CurrentDirectory;
        Assert.Null(PathUtils.SafeResolvePath(ws, null!));
        Assert.Null(PathUtils.SafeResolvePath(ws, ""));
        Assert.Null(PathUtils.SafeResolvePath(ws, "   "));
    }

    [Fact]
    public void SafeResolvePath_PathTraversal_ReturnsNull()
    {
        var ws = Environment.CurrentDirectory;
        Assert.Null(PathUtils.SafeResolvePath(ws, ".." + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "tmp"));
    }

    [Fact]
    public void SafeResolvePath_PrefixCollision_ReturnsNull()
    {
        var ws = Environment.CurrentDirectory;
        Assert.Null(PathUtils.SafeResolvePath(ws, ".." + Path.DirectorySeparatorChar + Path.GetFileName(ws) + "-extra" + Path.DirectorySeparatorChar + "secret.txt"));
    }

    [Fact]
    public void SafeResolvePath_ValidRelative_ReturnsResolvedPath()
    {
        var result = PathUtils.SafeResolvePath(Environment.CurrentDirectory, "test.txt");
        Assert.NotNull(result);
        Assert.EndsWith("test.txt", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequireSafePath_Escapes_Throws()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            PathUtils.RequireSafePath(Environment.CurrentDirectory, @"..\..\.." + Path.DirectorySeparatorChar + "tmp"));
    }

    [Fact]
    public void CheckFileSize_NonExistent_ReturnsError()
    {
        var result = PathUtils.CheckFileSize(Path.Combine(Path.GetTempPath(), "nonexistent_file_ltai_test.txt"));
        Assert.NotNull(result);
        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PathPermissionStore_GrantAndCheck_Works()
    {
        PathUtils.PathPermissionStore.Clear();
        var testPath = Path.Combine(Path.GetTempPath(), "ltai_granted_test.txt");
        Assert.False(PathUtils.PathPermissionStore.IsGranted(testPath));
        PathUtils.PathPermissionStore.Grant(testPath);
        Assert.True(PathUtils.PathPermissionStore.IsGranted(testPath));
        PathUtils.PathPermissionStore.Revoke(testPath);
        Assert.False(PathUtils.PathPermissionStore.IsGranted(testPath));
    }

    [Fact]
    public void TryResolveWithPermission_OutOfSandboxWithoutConfirm_ReturnsNull()
    {
        var result = PathUtils.TryResolveWithPermission(Environment.CurrentDirectory, @"..\..\.." + Path.DirectorySeparatorChar + "tmp");
        Assert.Null(result.resolvedPath);
        Assert.NotNull(result.deniedFullPath);
    }
}
