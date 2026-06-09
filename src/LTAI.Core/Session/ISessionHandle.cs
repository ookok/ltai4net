using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LTAI.Core.Session;

public interface ISessionHandle
{
    string Name { get; }
    string SerializeToJson();
    IReadOnlyList<ChatMessage> Messages { get; }
    string? ConversationId { get; }
    void UpdateFromJson(string json);
}

public interface ISessionSerializer
{
    string FileExtension { get; }
    string Serialize(JsonElement state);
    JsonElement Deserialize(string data);
}
