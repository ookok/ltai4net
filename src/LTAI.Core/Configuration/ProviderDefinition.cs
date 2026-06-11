namespace LTAI.Core.Configuration;

public sealed record ProviderDefinition(
    string EnvVar,
    string Service,
    string Description = "",
    string? Url = null,
    string? Endpoint = null,
    string? Model = null,
    decimal PriceInPerM = 0,
    decimal PriceOutPerM = 0,
    decimal PriceInCachePerM = 0);
