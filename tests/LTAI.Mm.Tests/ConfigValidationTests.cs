using LTAI.Core.Configuration;
using LTAI.Mm;
using Xunit;

namespace LTAI.Mm.Tests;

public class ConfigValidationTests
{
    [Fact]
    public void Valid_Options_Passes()
    {
        var options = new LTAIOptions();
        var errors = ConfigMmValidator.ValidateOptions(options);
        Assert.Empty(errors);
    }

    [Fact]
    public void Invalid_MaxTokens_Fails()
    {
        var options = new LTAIOptions { AI = new() { MaxTokens = 0 } };
        var errors = ConfigMmValidator.ValidateOptions(options);
        Assert.Contains(errors, e => e.Contains("MaxTokens") && e.Contains("minimum"));
    }

    [Fact]
    public void Invalid_Temperature_Low_Fails()
    {
        var options = new LTAIOptions { AI = new() { Temperature = -0.5 } };
        var errors = ConfigMmValidator.ValidateOptions(options);
        Assert.Contains(errors, e => e.Contains("Temperature") && e.Contains("minimum"));
    }

    [Fact]
    public void Invalid_Temperature_High_Fails()
    {
        var options = new LTAIOptions { AI = new() { Temperature = 3.0 } };
        var errors = ConfigMmValidator.ValidateOptions(options);
        Assert.Contains(errors, e => e.Contains("Temperature") && e.Contains("maximum"));
    }

    [Fact]
    public void Invalid_Port_Fails()
    {
        var options = new LTAIOptions { Web = new() { Port = 65536 } };
        var errors = ConfigMmValidator.ValidateOptions(options);
        Assert.Contains(errors, e => e.Contains("Port"));
    }

    [Fact]
    public void ThrowIfInvalid_Throws_On_Error()
    {
        var options = new LTAIOptions { AI = new() { MaxTokens = 0 } };
        Assert.Throws<InvalidOperationException>(() => ConfigMmValidator.ThrowIfInvalid(options));
    }
}
