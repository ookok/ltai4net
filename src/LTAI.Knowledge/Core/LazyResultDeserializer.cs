using System.Text;
using System.Text.Json;
using LTAI.Knowledge.Core.Models;

namespace LTAI.Knowledge.Core;

public interface ILazyResult
{
    string Id { get; }
    string Title { get; }
    string Domain { get; }
    double Score { get; }
    string Source { get; }
    int? ChunkIndex { get; }
    string Content { get; }
    bool IsLoaded { get; }
}

public sealed class LazyKnowledgeResult : ILazyResult
{
    private byte[] _rawContent;
    private string? _fullContent;
    private bool _contentLoaded;

    public string Id { get; }
    public string Title { get; }
    public string Domain { get; }
    public double Score { get; }
    public string Source { get; }
    public int? ChunkIndex { get; }
    public bool IsLoaded => _contentLoaded;

    public string Content
    {
        get
        {
            if (!_contentLoaded && _rawContent.Length > 0)
            {
                _fullContent = DeserializeContent(_rawContent);
                _contentLoaded = true;
                _rawContent = [];
            }
            return _fullContent ?? "";
        }
    }

    internal LazyKnowledgeResult(KnowledgeSearchResult source, byte[] rawContent)
    {
        Id = source.Id;
        Title = source.Title;
        Domain = source.Domain;
        Score = source.Score;
        Source = source.Source;
        ChunkIndex = source.ChunkIndex;
        _rawContent = rawContent;
        _fullContent = source.Content.Length > 0 ? source.Content : null;
        _contentLoaded = _fullContent != null;
    }

    internal LazyKnowledgeResult(string id, string title, string domain, double score,
        string source, int? chunkIndex, byte[] rawContent, string? fullContent)
    {
        Id = id;
        Title = title;
        Domain = domain;
        Score = score;
        Source = source;
        ChunkIndex = chunkIndex;
        _rawContent = rawContent;
        _fullContent = fullContent;
        _contentLoaded = fullContent != null;
    }

    private static string DeserializeContent(byte[] raw)
    {
        try
        {
            var reader = new Utf8JsonReader(raw);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName
                    && reader.ValueTextEquals("content"u8))
                {
                    reader.Read();
                    return reader.GetString() ?? "";
                }
            }
        }
        catch
        {
        }

        return Encoding.UTF8.GetString(raw);
    }

    public static implicit operator KnowledgeSearchResult(LazyKnowledgeResult lazy)
    {
        return new KnowledgeSearchResult
        {
            Id = lazy.Id,
            Title = lazy.Title,
            Content = lazy.Content,
            Domain = lazy.Domain,
            Score = lazy.Score,
            Source = lazy.Source,
            ChunkIndex = lazy.ChunkIndex
        };
    }
}

public sealed class LazyResultDeserializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public List<LazyKnowledgeResult> WrapBatch(IReadOnlyList<KnowledgeSearchResult> results)
    {
        var lazyResults = new List<LazyKnowledgeResult>(results.Count);

        foreach (var result in results)
        {
            byte[] rawContent;

            if (!string.IsNullOrEmpty(result.Content))
            {
                rawContent = SerializeField("content", result.Content);
            }
            else
            {
                rawContent = [];
            }

            lazyResults.Add(new LazyKnowledgeResult(result, rawContent));
        }

        return lazyResults;
    }

    public List<LazyKnowledgeResult> WrapFromJson(string json)
    {
        var results = JsonSerializer.Deserialize<List<KnowledgeSearchResult>>(json, SerializerOptions);
        return results != null ? WrapBatch(results) : new List<LazyKnowledgeResult>();
    }

    public KnowledgeSearchResult Materialize(ILazyResult lazy)
    {
        return lazy switch
        {
            LazyKnowledgeResult l => (KnowledgeSearchResult)l,
            _ => new KnowledgeSearchResult
            {
                Id = lazy.Id,
                Title = lazy.Title,
                Content = lazy.Content,
                Domain = lazy.Domain,
                Score = lazy.Score,
                Source = lazy.Source,
                ChunkIndex = lazy.ChunkIndex
            }
        };
    }

    public List<KnowledgeSearchResult> MaterializeAll(IReadOnlyList<ILazyResult> lazyResults)
    {
        return lazyResults.Select(Materialize).ToList();
    }

    public static string ExtractContentFast(byte[] raw)
    {
        try
        {
            var reader = new Utf8JsonReader(raw);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName
                    && reader.ValueTextEquals("content"u8))
                {
                    reader.Read();
                    return reader.GetString() ?? "";
                }
            }
        }
        catch
        {
        }

        return "";
    }

    public static string ExtractFieldFast(byte[] raw, string fieldName)
    {
        try
        {
            var fieldBytes = Encoding.UTF8.GetBytes(fieldName);
            var reader = new Utf8JsonReader(raw);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName
                    && reader.ValueTextEquals(fieldBytes))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                        return reader.GetString() ?? "";
                    if (reader.TokenType == JsonTokenType.Number)
                        return reader.TryGetInt64(out var l) ? l.ToString() :
                            reader.TryGetDouble(out var d) ? d.ToString() : "0";
                    reader.Skip();
                }
            }
        }
        catch
        {
        }

        return "";
    }

    private static byte[] SerializeField(string fieldName, string value)
    {
        var json = $"{{\"{fieldName}\":{JsonSerializer.Serialize(value)}}}";
        return Encoding.UTF8.GetBytes(json);
    }
}
