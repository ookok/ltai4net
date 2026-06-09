using System.Text.Json;
using LTAI.Mm.Core;
using LTAI.Mm.Tree;

namespace LTAI.Mm;

public static class KgStoreBridge
{
    public static byte[] SerializeProps(Dictionary<string, object?>? props)
    {
        if (props == null || props.Count == 0) return [];
        var encoder = new WireEncoder();
        var inner = new WireEncoder();
        foreach (var (key, value) in props)
        {
            inner.EncodeString(key);
            EncodeValue(inner, value);
        }
        encoder.EncodeObjectPayload(inner.ToByteArray());
        return encoder.ToByteArray();
    }

    public static string SerializePropsToBase64(Dictionary<string, object?>? props)
    {
        var bytes = SerializeProps(props);
        return bytes.Length > 0 ? Convert.ToBase64String(bytes) : "";
    }

    public static Dictionary<string, object?>? DeserializeProps(byte[] data)
    {
        if (data == null || data.Length == 0) return null;
        try
        {
            var decoder = new WireDecoder(data);
            var result = decoder.Decode();
            var map = result.AsMap();
            if (map == null) return null;

            var dict = new Dictionary<string, object?>();
            foreach (var (key, value) in map)
                dict[key] = UnwrapValue(value);
            return dict;
        }
        catch { return null; }
    }

    public static Dictionary<string, object?>? DeserializePropsFromBase64(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try { return DeserializeProps(Convert.FromBase64String(base64)); }
        catch { return null; }
    }

    private static object? UnwrapValue(WireDecoder.IDecodeResult result)
    {
        if (result.IsNull) return null;
        if (result.AsArray() is { } arr)
        {
            var list = new List<object?>();
            foreach (var item in arr) list.Add(UnwrapValue(item));
            return list;
        }
        if (result.AsMap() is { } map)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var (key, value) in map) dict[key] = UnwrapValue(value);
            return dict;
        }
        if (result.ValueKind == MmValueType.Str ||
            result.ValueKind == MmValueType.Email ||
            result.ValueKind == MmValueType.Url)
            return result.AsString();
        if (result.ValueKind == MmValueType.Bool)
            return result.AsBool();
        if (result.ValueKind == MmValueType.F64 || result.ValueKind == MmValueType.F32)
            return result.AsDouble();
        if (result.ValueKind == MmValueType.U64)
            return (ulong)result.AsObject()!;

        long val = result.AsInt64();
        if (val >= int.MinValue && val <= int.MaxValue) return (int)val;
        return val;
    }

    private static void EncodeValue(WireEncoder enc, object? value)
    {
        if (value == null) { enc.EncodeNull(); return; }
        switch (value)
        {
            case bool b: enc.EncodeBool(b); break;
            case int i: enc.EncodeInt32(i); break;
            case long l: enc.EncodeInt64(l); break;
            case double d: enc.EncodeDouble(d); break;
            case string s: enc.EncodeString(s); break;
            case byte[] bts: enc.EncodeBytes(bts); break;
            case System.Collections.IList list:
                {
                    var inner = new WireEncoder();
                    foreach (var item in list) EncodeValue(inner, item);
                    enc.EncodeArrayPayload(inner.ToByteArray());
                    break;
                }
            case System.Collections.IDictionary dict:
                {
                    var inner = new WireEncoder();
                    foreach (System.Collections.DictionaryEntry entry in dict)
                    {
                        EncodeValue(inner, entry.Key?.ToString() ?? "");
                        EncodeValue(inner, entry.Value);
                    }
                    enc.EncodeObjectPayload(inner.ToByteArray());
                    break;
                }
            default: enc.EncodeString(value.ToString() ?? ""); break;
        }
    }
}
