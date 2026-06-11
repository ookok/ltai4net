#pragma warning disable IL2067 // NativeAOT IL warnings — reflection-based MM codec

using System.Collections;
using System.Reflection;
using LTAI.Mm.Core;
using LTAI.Mm.Ir;
using LTAI.Mm.Tree;

namespace LTAI.Mm.Reflection;

internal static class EncoderPool
{
    [ThreadStatic] private static WireEncoder? _shared;
    [ThreadStatic] private static WireEncoder? _inner;

    internal static WireEncoder Rent() => Interlocked.Exchange(ref _shared, null) ?? new WireEncoder();
    internal static void Return(WireEncoder enc) { enc.Reset(); _shared = enc; }

    internal static WireEncoder RentInner() => Interlocked.Exchange(ref _inner, null) ?? new WireEncoder();
    internal static void ReturnInner(WireEncoder enc) { enc.Reset(); _inner = enc; }
}

internal static class DefaultValueChecker
{
    internal static bool IsDefaultForType(this object? value, Type type)
    {
        if (value == null) return true;
        if (type.IsValueType)
            return value.Equals(Activator.CreateInstance(type));
        return false;
    }
}

public static class ReflectEncoder
{
    public static byte[] EncodeToBytes(object? value, string? tag = null)
    {
        var encoder = EncoderPool.Rent();
        try
        {
            if (!string.IsNullOrEmpty(tag))
            {
                var tagObj = Tag.Parse(tag);
                var payloadEnc = EncoderPool.RentInner();
                try
                {
                    EncodeValue(payloadEnc, value);
                    encoder.EncodeTaggedPayload(payloadEnc.ToByteArray(), tagObj.ToBytes());
                }
                finally { EncoderPool.ReturnInner(payloadEnc); }
            }
            else
            {
                EncodeValue(encoder, value);
            }
            return encoder.ToByteArray();
        }
        finally { EncoderPool.Return(encoder); }
    }

    public static INode ValueToNode(object? value)
    {
        return ObjectToNode(value);
    }

    public static void EncodeValue(WireEncoder enc, object? value)
    {
        if (value == null) { enc.EncodeNull(); return; }

        switch (value)
        {
            case bool b: enc.EncodeBool(b); break;
            case sbyte sb: enc.EncodeInt8(sb); break;
            case byte ub: enc.EncodeUInt8(ub); break;
            case short s: enc.EncodeInt16(s); break;
            case ushort us: enc.EncodeUInt16(us); break;
            case int i: enc.EncodeInt32(i); break;
            case uint ui: enc.EncodeUInt32(ui); break;
            case long l: enc.EncodeInt64(l); break;
            case ulong ul: enc.EncodeUInt64(ul); break;
            case float f: enc.EncodeFloat(f); break;
            case double d: enc.EncodeDouble(d); break;
            case decimal dec: enc.EncodeBigIntDecimal(dec.ToString()); break;
            case string str: enc.EncodeString(str); break;
            case byte[] bytes: enc.EncodeBytes(bytes); break;
            case DateTime dt: enc.EncodeDateTime(dt); break;
            case Enum e: enc.EncodeString(e.ToString()); break;
            case IList list:
                {
                    var inner = EncoderPool.RentInner();
                    try
                    {
                        foreach (var item in list) EncodeValue(inner, item);
                        enc.EncodeArrayPayload(inner.ToByteArray());
                    }
                    finally { EncoderPool.ReturnInner(inner); }
                    break;
                }
            case IDictionary dict:
                {
                    var inner = EncoderPool.RentInner();
                    try
                    {
                        foreach (DictionaryEntry entry in dict)
                        {
                            EncodeValue(inner, entry.Key?.ToString() ?? "");
                            EncodeValue(inner, entry.Value);
                        }
                        enc.EncodeObjectPayload(inner.ToByteArray());
                    }
                    finally { EncoderPool.ReturnInner(inner); }
                    break;
                }
            default:
                EncodeObject(enc, value);
                break;
        }
    }

    private static void EncodeObject(WireEncoder enc, object value)
    {
        var cached = TypeMetadataCache.Get(value.GetType());
        var payloadEnc = EncoderPool.RentInner();
        try
        {
            foreach (var prop in cached.Properties)
            {
                EncodeValue(payloadEnc, prop.Name);
                var propValue = prop.Getter(value);

                if (prop.MmTag != null)
                {
                    var innerEnc = EncoderPool.RentInner();
                    try
                    {
                        EncodeValue(innerEnc, propValue);
                        var tagBytes = prop.MmTag.ToBytes();
                        payloadEnc.EncodeTaggedPayload(innerEnc.ToByteArray(), tagBytes);
                    }
                    finally { EncoderPool.ReturnInner(innerEnc); }
                }
                else
                {
                    EncodeValue(payloadEnc, propValue);
                }
            }
            enc.EncodeObjectPayload(payloadEnc.ToByteArray());
        }
        finally { EncoderPool.ReturnInner(payloadEnc); }
    }

    private static INode ObjectToNode(object? value)
    {
        if (value == null) return new NodeScalar(null, MmValueType.Unknown, "null");

        var type = value.GetType();

        if (type.IsPrimitive || value is string or decimal or DateTime or Guid)
            return new NodeScalar(value, TypeInfer.Infer(type), value.ToString() ?? "");

        if (value is IList list)
        {
            var node = new MmArray();
            foreach (var item in list) node.Children.Add(ObjectToNode(item));
            return node;
        }

        if (value is IDictionary dict)
        {
            var node = new MmMap();
            foreach (DictionaryEntry entry in dict)
                node.Entries.Add(new MmMapEntry(
                    new NodeScalar(entry.Key, MmValueType.Str, entry.Key?.ToString() ?? ""),
                    ObjectToNode(entry.Value)));
            return node;
        }

        var cached = TypeMetadataCache.Get(type);
        var objNode = new MmMap();
        foreach (var prop in cached.Properties)
        {
            objNode.Entries.Add(new MmMapEntry(
                new NodeScalar(prop.Name, MmValueType.Str, prop.Name),
                ObjectToNode(prop.Getter(value))));
        }
        return objNode;
    }
}
