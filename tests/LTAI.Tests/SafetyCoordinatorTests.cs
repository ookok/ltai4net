using LTAI.Core.Safety;
using Xunit;

namespace LTAI.Tests;

public sealed class SafetyCoordinatorTests
{
    [Fact]
    public void ConsumeBlock_InitialCall_ReturnsNull()
    {
        SafetyCoordinator.ConsumeBlock();
        Assert.Null(SafetyCoordinator.ConsumeBlock());
    }

    [Fact]
    public void ConsumeBlock_DoubleCall_SecondReturnsNull()
    {
        SafetyCoordinator.ConsumeBlock();
        SafetyCoordinator.ConsumeBlock();
        Assert.Null(SafetyCoordinator.ConsumeBlock());
    }
}

public sealed class SafetyRulesIntegrationTests
{
    [Theory]
    [InlineData("eval(base64_decode('c3lzdGVtKCdybSAtcmYgLycp'))", false)]
    [InlineData("Read ../../../etc/passwd", true)]
    [InlineData("How do I write a loop in Python?", true)]
    [InlineData("你好", true)]
    [InlineData("git commit -m 'fix'", true)]
    [InlineData("SELECT * FROM users", true)]
    [InlineData("DROP TABLE users; --", false)]
    [InlineData("<script>alert('xss')</script>", false)]
    [InlineData("my api_key a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5", false)]
    [InlineData("4111 1111 1111 1111", false)]
    [InlineData("+86 138 0013 8000", false)]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", false)]
    public void IsSafeByRules_VariousInputs_ReturnsExpected(string input, bool expected)
    {
        Assert.Equal(expected, SafetyRules.IsSafeByRules(input));
    }
}
