using Microsoft.Extensions.Options;

namespace LTAI.Core.Configuration;

/// <summary>
/// Startup-time validator for <see cref="LTAIOptions"/>.
/// Catches misconfiguration before the app starts.
/// 
/// Offline mode: when <c>AI.DefaultProvider</c> is empty, validation still passes
/// (returns Success) so the UI can launch. The app enters offline mode and users
/// can configure a provider via the settings UI.
/// </summary>
public sealed class LTAIOptionsValidator : IValidateOptions<LTAIOptions>
{
    public ValidateOptionsResult Validate(string? name, LTAIOptions options)
    {
        var errors = new List<string>();

        // Offline mode: empty DefaultProvider is allowed (UI shows config screen)
        if (options.AI.DefaultProvider is not null && options.AI.DefaultProvider.Length > 100)
            errors.Add("AI.DefaultProvider exceeds maximum length (100 chars)");

        if (options.AI.MaxTokens is < 0 or > 1_000_000)
            errors.Add($"AI.MaxTokens must be between 0 and 1,000,000 (got {options.AI.MaxTokens})");

        if (options.AI.Temperature is < 0 or > 2)
            errors.Add($"AI.Temperature must be between 0.0 and 2.0 (got {options.AI.Temperature})");

        if (options.AI.GlobalTokenBudget < 0)
            errors.Add($"AI.GlobalTokenBudget must be >= 0 (got {options.AI.GlobalTokenBudget})");

        if (options.AI.PerUserTokenBudget < 0)
            errors.Add($"AI.PerUserTokenBudget must be >= 0 (got {options.AI.PerUserTokenBudget})");

        // Web Config (only validate if port is set; default 5100 is always valid)
        if (options.Web.Port is < 1 or > 65535)
            errors.Add($"Web.Port must be between 1 and 65535 (got {options.Web.Port})");

        // Data directories
        if (string.IsNullOrWhiteSpace(options.DataDirectory))
            errors.Add("DataDirectory must not be empty");

        if (options.MaxHistoryMessages is < 1 or > 10000)
            errors.Add($"MaxHistoryMessages must be between 1 and 10,000 (got {options.MaxHistoryMessages})");

        if (errors.Count > 0)
            return ValidateOptionsResult.Fail(errors);

        return ValidateOptionsResult.Success;
    }
}
