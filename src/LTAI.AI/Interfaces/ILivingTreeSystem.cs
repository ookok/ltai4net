using LTAI.AI.Governors;
using Microsoft.Extensions.AI;

namespace LTAI.AI.Interfaces;

public interface ILivingTreeSystem
{
    LTAI.Models.SystemMode Mode { get; }
    bool DNAEnabled { get; }
    IChatClient LLMClient { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<string> ChatAsync(string query, CancellationToken cancellationToken = default);
    IAsyncEnumerable<string> StreamChatAsync(string query, CancellationToken cancellationToken = default);
    IAsyncEnumerable<string> StreamWithModelAsync(string query, string model, CancellationToken cancellationToken = default);
    Task<GovernorOutput> ProcessTypedAsync(GovernorInput input, CancellationToken cancellationToken = default);
}
