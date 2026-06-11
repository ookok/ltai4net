using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace LTAI.Core.Session;

public sealed class CompressedSessionSerializer : ISessionSerializer
{
    private readonly ISessionSerializer _inner;
    private readonly CompressionLevel _level;

    public CompressedSessionSerializer(ISessionSerializer inner, CompressionLevel level = CompressionLevel.Fastest)
    {
        _inner = inner;
        _level = level;
    }

    public string FileExtension => _inner.FileExtension + ".gz";

    public string Serialize(JsonElement state)
    {
        var innerData = _inner.Serialize(state);
        var bytes = Encoding.UTF8.GetBytes(innerData);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, _level, leaveOpen: true))
            gzip.Write(bytes);
        return Convert.ToBase64String(output.ToArray());
    }

    public JsonElement Deserialize(string data)
    {
        var compressed = Convert.FromBase64String(data);
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        var innerData = reader.ReadToEnd();
        return _inner.Deserialize(innerData);
    }
}
