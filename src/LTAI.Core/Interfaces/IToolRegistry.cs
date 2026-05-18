using LTAI.Core.Models;

namespace LTAI.Core.Interfaces;

public interface IToolRegistry
{
    Task RegisterAsync(string toolName, Func<Dictionary<string, object?>, Task<object?>> handler, CancellationToken cancellationToken = default);
    Task<object?> InvokeAsync(string toolName, Dictionary<string, object?> parameters, CancellationToken cancellationToken = default);
    bool HasTool(string toolName);
    IEnumerable<string> ListTools();
}
