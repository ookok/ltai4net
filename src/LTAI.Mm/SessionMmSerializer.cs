using System.Text.Json;
using LTAI.Core.Session;

namespace LTAI.Mm;

public sealed class MmSessionSerializer : ISessionSerializer
{
    public string FileExtension => ".mm";

    public string Serialize(JsonElement state)
    {
        var bytes = SessionBridge.JsonElementToMm(state);
        return Convert.ToBase64String(bytes);
    }

    public JsonElement Deserialize(string data)
    {
        var bytes = Convert.FromBase64String(data);
        return SessionBridge.MmToJsonElement(bytes);
    }
}
