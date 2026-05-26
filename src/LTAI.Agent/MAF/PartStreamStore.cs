using System.Text.Json;
using System.Text.Json.Serialization;
using LTAI.Knowledge.Core;
using LTAI.Models;

namespace LTAI.Agent.MAF;

public sealed class PartStreamStore
{
    private readonly string _sessionsRoot;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new PartJsonConverter() }
    };

    public PartStreamStore(string workspaceRoot)
    {
        _sessionsRoot = Path.Combine(OptionService.Get("paths.livingtree") ?? Path.Combine(workspaceRoot, ".livingtree"), "sessions");
        Directory.CreateDirectory(_sessionsRoot);
    }

    public async Task AppendAsync(string sessionId, Part part, CancellationToken ct = default)
    {
        var path = GetSessionPath(sessionId);
        var json = JsonSerializer.Serialize(part, _jsonOptions);
        var line = json + "\n";
        await File.AppendAllTextAsync(path, line, ct).ConfigureAwait(false);
    }

    public async Task<List<Part>> ReplayAsync(string sessionId, CancellationToken ct = default)
    {
        var path = GetSessionPath(sessionId);
        if (!File.Exists(path)) return [];

        var parts = new List<Part>();
        var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var part = JsonSerializer.Deserialize<Part>(line, _jsonOptions);
                if (part != null) parts.Add(part);
            }
            catch { /* skip corrupt lines */ }
        }
        return parts;
    }

    public void ForkSession(string sourceSessionId, string newSessionId)
    {
        var source = GetSessionPath(sourceSessionId);
        var dest = GetSessionPath(newSessionId);
        if (File.Exists(source))
            File.Copy(source, dest, overwrite: false);
    }

    public List<string> GetSessionIds()
    {
        try
        {
            return Directory.GetFiles(_sessionsRoot, "*.jsonl")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .OrderByDescending(f => new FileInfo(Path.Combine(_sessionsRoot, f + ".jsonl")).LastWriteTime)
                .ToList();
        }
        catch { return []; }
    }

    private string GetSessionPath(string sessionId) =>
        Path.Combine(_sessionsRoot, $"{sessionId}.jsonl");
}

public sealed class PartJsonConverter : JsonConverter<Part>
{
    public override Part? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var typeStr = root.TryGetProperty("__type", out var t) ? t.GetString() : null;

        return typeStr switch
        {
            "Text" => JsonSerializer.Deserialize<TextPart>(root.GetRawText(), options),
            "Reasoning" => JsonSerializer.Deserialize<ReasoningPart>(root.GetRawText(), options),
            "ToolInvocation" => JsonSerializer.Deserialize<ToolInvocationPart>(root.GetRawText(), options),
            "File" => JsonSerializer.Deserialize<FilePart>(root.GetRawText(), options),
            "Agent" => JsonSerializer.Deserialize<AgentPart>(root.GetRawText(), options),
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, Part value, JsonSerializerOptions options)
    {
        var typeName = value switch
        {
            TextPart => "Text",
            ReasoningPart => "Reasoning",
            ToolInvocationPart => "ToolInvocation",
            FilePart => "File",
            AgentPart => "Agent",
            _ => "Unknown"
        };

        var json = JsonSerializer.Serialize(value, value.GetType(), options);
        using var doc = JsonDocument.Parse(json);
        writer.WriteStartObject();
        writer.WriteString("__type", typeName);
        foreach (var prop in doc.RootElement.EnumerateObject())
            prop.WriteTo(writer);
        writer.WriteEndObject();
    }
}
