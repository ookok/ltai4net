using System.Text.Json;

namespace LTAI.Core.Serialization;

public sealed class MsgPackSerializer
{
    public static byte[] Serialize<T>(T obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return global::System.Text.Encoding.UTF8.GetBytes(json);
    }

    public static T? Deserialize<T>(byte[] data) where T : class
    {
        var json = global::System.Text.Encoding.UTF8.GetString(data);
        return JsonSerializer.Deserialize<T>(json);
    }
}

public static class JsonUtils
{
    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false
    };

    public static string Dumps(object obj, bool indent = true)
    {
        return JsonSerializer.Serialize(obj, indent ? PrettyOptions : CompactOptions);
    }

    public static T? Loads<T>(string json) where T : class
    {
        return JsonSerializer.Deserialize<T>(json);
    }

    public static object? Loads(string json)
    {
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    public static void Dump(object obj, string path)
    {
        var dir = global::System.IO.Path.GetDirectoryName(path);
        if (dir != null) global::System.IO.Directory.CreateDirectory(dir);
        global::System.IO.File.WriteAllText(path, Dumps(obj));
    }

    public static T? Load<T>(string path) where T : class
    {
        var json = global::System.IO.File.ReadAllText(path);
        return Loads<T>(json);
    }
}
