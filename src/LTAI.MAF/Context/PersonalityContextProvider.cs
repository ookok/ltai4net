using LTAI.MAF.Context;

namespace LTAI.MAF;

public sealed class PersonalityContextProvider : LTAIContextProvider
{
    public PersonalityContextProvider() : base("Personality", ContextProviderType.Memory) { }

    public override Task<IReadOnlyList<ContextItem>> GetContextAsync(string query, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ContextItem>>(Array.Empty<ContextItem>());
    }
}
