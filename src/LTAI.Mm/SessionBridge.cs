using System.Text.Json;
using LTAI.Mm.Core;
using LTAI.Mm.Ir;
using LTAI.Mm.Tree;

namespace LTAI.Mm;

public static class SessionBridge
{
    public static byte[] JsonElementToMm(JsonElement element)
    {
        var node = JsonElementToNode(element);
        var encoder = new WireEncoder();
        EncodeNode(encoder, node);
        return encoder.ToByteArray();
    }

    public static JsonElement MmToJsonElement(byte[] data)
    {
        var decoder = new WireDecoder(data);
        var result = decoder.Decode();
        var node = ToNode(result);
        return NodeToJsonElement(node);
    }

    public static string MmToJsonString(byte[] data)
    {
        var el = MmToJsonElement(data);
        return el.GetRawText();
    }

    public static byte[] JsonStringToMm(string json)
    {
        var doc = JsonDocument.Parse(json);
        return JsonElementToMm(doc.RootElement);
    }

    private static INode JsonElementToNode(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
                return new NodeScalar(null, MmValueType.Unknown, "null");
            case JsonValueKind.True:
                return new NodeScalar(true, MmValueType.Bool, "true");
            case JsonValueKind.False:
                return new NodeScalar(false, MmValueType.Bool, "false");
            case JsonValueKind.Number:
                if (el.TryGetInt64(out var l))
                    return new NodeScalar(l, MmValueType.I64, l.ToString());
                return new NodeScalar(el.GetDouble(), MmValueType.F64, el.GetRawText());
            case JsonValueKind.String:
                return new NodeScalar(el.GetString(), MmValueType.Str, el.GetString() ?? "");
            case JsonValueKind.Array:
                var arr = new MmArray();
                foreach (var item in el.EnumerateArray())
                    arr.Children.Add(JsonElementToNode(item));
                return arr;
            case JsonValueKind.Object:
                var map = new MmMap();
                foreach (var prop in el.EnumerateObject())
                    map.Entries.Add(new MmMapEntry(
                        new NodeScalar(prop.Name, MmValueType.Str, prop.Name),
                        JsonElementToNode(prop.Value)));
                return map;
            default:
                return new NodeScalar(null, MmValueType.Unknown, "null");
        }
    }

    private static JsonElement NodeToJsonElement(INode node)
    {
        switch (node)
        {
            case NodeScalar scalar:
                if (scalar.Data == null) return JsonDocument.Parse("null").RootElement;
                return scalar.Kind switch
                {
                    MmValueType.Bool => JsonDocument.Parse(scalar.Text.ToLowerInvariant()).RootElement,
                    MmValueType.Str or MmValueType.Email or MmValueType.Url or MmValueType.Ip or
                    MmValueType.Uuid or MmValueType.Enums or MmValueType.DateTime or MmValueType.Date or MmValueType.Time =>
                        JsonDocument.Parse($"\"{EscapeJson(scalar.Text)}\"").RootElement,
                    _ => JsonDocument.Parse(scalar.Text).RootElement,
                };

            case MmArray arr:
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append('[');
                    for (int i = 0; i < arr.Children.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(NodeToJsonString(arr.Children[i]));
                    }
                    sb.Append(']');
                    return JsonDocument.Parse(sb.ToString()).RootElement;
                }

            case MmMap map:
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append('{');
                    for (int i = 0; i < map.Entries.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append($"\"{EscapeJson(map.Entries[i].Key.Text)}\":");
                        sb.Append(NodeToJsonString(map.Entries[i].Value));
                    }
                    sb.Append('}');
                    return JsonDocument.Parse(sb.ToString()).RootElement;
                }

            default:
                return JsonDocument.Parse("null").RootElement;
        }
    }

    private static string NodeToJsonString(INode node)
    {
        switch (node)
        {
            case NodeScalar scalar:
                if (scalar.Data == null) return "null";
                return scalar.Kind switch
                {
                    MmValueType.Bool => scalar.Text.ToLowerInvariant(),
                    MmValueType.Str or MmValueType.Email or MmValueType.Url or MmValueType.Ip or
                    MmValueType.Uuid or MmValueType.Enums or MmValueType.DateTime or MmValueType.Date or MmValueType.Time =>
                        $"\"{EscapeJson(scalar.Text)}\"",
                    _ => scalar.Text,
                };
            case MmArray arr:
                {
                    var sb = new System.Text.StringBuilder("[");
                    for (int i = 0; i < arr.Children.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(NodeToJsonString(arr.Children[i]));
                    }
                    sb.Append(']');
                    return sb.ToString();
                }
            case MmMap map:
                {
                    var sb = new System.Text.StringBuilder("{");
                    for (int i = 0; i < map.Entries.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append($"\"{EscapeJson(map.Entries[i].Key.Text)}\":");
                        sb.Append(NodeToJsonString(map.Entries[i].Value));
                    }
                    sb.Append('}');
                    return sb.ToString();
                }
            default:
                return "null";
        }
    }

    internal static void EncodeNode(WireEncoder enc, INode node)
    {
        switch (node)
        {
            case NodeScalar scalar:
                if (scalar.Data == null) { enc.EncodeNull(); return; }
                switch (scalar.Kind)
                {
                    case MmValueType.Bool: enc.EncodeBool((bool)scalar.Data); break;
                    case MmValueType.Str or MmValueType.Email or MmValueType.Url or MmValueType.Ip or
                         MmValueType.Uuid or MmValueType.Enums: enc.EncodeString(scalar.Text); break;
                    case MmValueType.I or MmValueType.I8 or MmValueType.I16 or MmValueType.I32 or MmValueType.I64:
                        enc.EncodeInt64((long)scalar.Data); break;
                    case MmValueType.U or MmValueType.U8 or MmValueType.U16 or MmValueType.U32 or MmValueType.U64:
                        enc.EncodeUInt64((ulong)scalar.Data); break;
                    case MmValueType.F32: enc.EncodeFloat((float)scalar.Data); break;
                    case MmValueType.F64: enc.EncodeDouble((double)scalar.Data); break;
                    default: enc.EncodeString(scalar.Text); break;
                }
                break;

            case MmArray arr:
                {
                    var inner = new WireEncoder();
                    foreach (var child in arr.Children)
                        EncodeNode(inner, child);
                    enc.EncodeArrayPayload(inner.ToByteArray());
                    break;
                }

            case MmMap map:
                {
                    var inner = new WireEncoder();
                    foreach (var entry in map.Entries)
                    {
                        inner.EncodeString(entry.Key.Text);
                        EncodeNode(inner, entry.Value);
                    }
                    enc.EncodeObjectPayload(inner.ToByteArray());
                    break;
                }
        }
    }

    private static INode ToNode(WireDecoder.IDecodeResult result)
    {
        if (result.IsNull)
            return new NodeScalar(null, MmValueType.Unknown, "null");

        if (result.AsArray() is { } arr)
        {
            var node = new MmArray();
            foreach (var item in arr) node.Children.Add(ToNode(item));
            return node;
        }

        if (result.AsMap() is { } map)
        {
            var node = new MmMap();
            foreach (var (key, value) in map)
                node.Entries.Add(new MmMapEntry(
                    new NodeScalar(key, MmValueType.Str, key),
                    ToNode(value)));
            return node;
        }

        string text = result.ValueKind switch
        {
            MmValueType.Str or MmValueType.Email or MmValueType.Url or MmValueType.Ip or
            MmValueType.Uuid or MmValueType.Bytes or MmValueType.Enums or MmValueType.Media =>
                result.AsString(),
            _ => result.AsObject()?.ToString() ?? "null",
        };
        return new NodeScalar(result.AsObject(), result.ValueKind, text);
    }

    private static string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
    }
}
