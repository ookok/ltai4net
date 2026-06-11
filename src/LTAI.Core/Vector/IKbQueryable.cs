namespace LTAI.Core.Vector;

public interface IKbQueryable
{
    Task<List<string>> QueryAsync(string query, int topK = 10, CancellationToken ct = default);
}
