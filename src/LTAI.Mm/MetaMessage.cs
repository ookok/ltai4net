using LTAI.Mm.Core;
using LTAI.Mm.Ir;
using LTAI.Mm.Jsonc;
using LTAI.Mm.Reflection;
using LTAI.Mm.Tree;

namespace LTAI.Mm;

public static class MetaMessage
{
    public static byte[] Encode<T>(T value) =>
        ReflectEncoder.EncodeToBytes(value);

    public static byte[] Encode(object value, Type type) =>
        ReflectEncoder.EncodeToBytes(value);

    public static T Decode<T>(byte[] data) =>
        ReflectBinder.Decode<T>(data);

    public static void Decode(byte[] data, object target) =>
        ReflectBinder.Bind(data, target);

    public static byte[] FromValue(object value, string? tag = null) =>
        ReflectEncoder.EncodeToBytes(value, tag);

    public static INode DecodeToTree(byte[] data)
    {
        var decoder = new WireDecoder(data);
        var result = decoder.Decode();
        return ReflectBinder.ResultToNode(result);
    }

    public static string DecodeToJsonc(byte[] data)
    {
        var node = DecodeToTree(data);
        return JsoncEmitter.ToJsonc(node);
    }

    public static string ValueToJsonc(object value)
    {
        var node = (value is INode inode) ? inode : ReflectEncoder.ValueToNode(value);
        return JsoncEmitter.ToJsonc(node);
    }

    public static INode ParseJsonc(string jsonc) =>
        JsoncParser.Parse(jsonc);

    public static T FromJsonc<T>(string jsonc) where T : new() =>
        JsoncParser.Bind<T>(jsonc);

    public static void FromJsonc(string jsonc, object target) =>
        JsoncParser.Bind(jsonc, target);

    public static byte[] FromJsoncToBytes(string jsonc)
    {
        var node = JsoncParser.Parse(jsonc);
        return EncodeNode(node);
    }

    public static byte[] EncodeNode(INode node)
    {
        var encoder = new WireEncoder();
        EncodeTree(encoder, node);
        return encoder.ToByteArray();
    }

    private static void EncodeTree(WireEncoder enc, INode node)
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
                    case MmValueType.F32: enc.EncodeFloat((float)scalar.Data); break;
                    case MmValueType.F64: enc.EncodeDouble((double)scalar.Data); break;
                    default: enc.EncodeString(scalar.Text); break;
                }
                break;

            case MmArray arr:
                {
                    var inner = new WireEncoder();
                    foreach (var child in arr.Children)
                        EncodeTree(inner, child);
                    if (arr.Tag != null)
                    {
                        var tagBytes = arr.Tag.ToBytes();
                        var payload = inner.ToByteArray();
                        var taggedEnc = new WireEncoder();
                        taggedEnc.EncodeTaggedPayload(payload, tagBytes);
                        enc.EncodeArrayPayload(taggedEnc.ToByteArray());
                    }
                    else
                    {
                        enc.EncodeArrayPayload(inner.ToByteArray());
                    }
                    break;
                }

            case MmMap map:
                {
                    var inner = new WireEncoder();
                foreach (var entry in map.Entries)
                {
                    inner.EncodeString(entry.Key.Text);
                    if (entry.Value.Tag != null)
                    {
                        var valEnc = new WireEncoder();
                        EncodeTree(valEnc, entry.Value);
                        var tagBytes = entry.Value.Tag.ToBytes();
                        inner.EncodeTaggedPayload(valEnc.ToByteArray(), tagBytes);
                    }
                    else
                    {
                        EncodeTree(inner, entry.Value);
                    }
                }
                enc.EncodeObjectPayload(inner.ToByteArray());
                    break;
                }
        }
    }

    public static ValidationResult Validate(object value, string tagString) =>
        Core.Validator.Validate(value, tagString);

    public static ValidationResult Validate(object value, Tag tag) =>
        Core.Validator.Validate(value, tag);

    public static MmValueType InferType(Type type) =>
        TypeInfer.Infer(type);
}
