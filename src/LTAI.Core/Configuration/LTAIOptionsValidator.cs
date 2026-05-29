using Microsoft.Extensions.Options;

namespace LTAI.Core.Configuration;

/// <summary>
/// Validates <see cref="LTAIOptions"/> at startup to catch misconfiguration early.
/// Registered via <c>services.AddSingleton&lt;IValidateOptions&lt;LTAIOptions&gt;, LTAIOptionsValidator&gt;()</c>.
/// </summary>
public sealed class LTAIOptionsValidator : IValidateOptions<LTAIOptions>
{
    public ValidateOptionsResult Validate(string? name, LTAIOptions options)
    {
        var errors = new List<string>();

        // AI Config
        if (string.IsNullOrWhiteSpace(options.AI.DefaultProvider))
            errors.Add("AI.DefaultProvider must not be empty");

        if (options.AI.MaxTokens <= 0 || options.AI.MaxTokens > 1_000_000)
            errors.Add($"AI.MaxTokens must be between 1 and 1,000,000 (got {options.AI.MaxTokens})");

        if (options.AI.Temperature is < 0 or > 2)
            errors.Add($"AI.Temperature must be between 0.0 and 2.0 (got {options.AI.Temperature})");

        if (options.AI.GlobalTokenBudget <= 0)
            errors.Add($"AI.GlobalTokenBudget must be > 0 (got {options.AI.GlobalTokenBudget})");

        if (options.AI.PerUserTokenBudget <= 0)
            errors.Add($"AI.PerUserTokenBudget must be > 0 (got {options.AI.PerUserTokenBudget})");

        // Web Config
        if (options.Web.Port is < 1 or > 65535)
            errors.Add($"Web.Port must be between 1 and 65535 (got {options.Web.Port})");

        // Data directories
        if (string.IsNullOrWhiteSpace(options.DataDirectory))
            errors.Add("DataDirectory must not be empty");

        if (options.MaxHistoryMessages is < 1 or > 10000)
            errors.Add($"MaxHistoryMessages must be between 1 and 10,000 (got {options.MaxHistoryMessages})");

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
