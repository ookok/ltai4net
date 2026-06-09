using LTAI.Mm;
using LTAI.Mm.Core;
using LTAI.Mm.Ir;
using Xunit;

namespace LTAI.Mm.Tests;

public class WireTests
{
    [Fact]
    public void EncodeDecode_Bool_True()
    {
        var enc = new WireEncoder();
        enc.EncodeBool(true);
        var dec = new WireDecoder(enc.ToByteArray());
        var result = dec.Decode();
        Assert.True(result.AsBool());
    }

    [Fact]
    public void EncodeDecode_Bool_False()
    {
        var enc = new WireEncoder();
        enc.EncodeBool(false);
        var dec = new WireDecoder(enc.ToByteArray());
        var result = dec.Decode();
        Assert.False(result.AsBool());
    }

    [Fact]
    public void EncodeDecode_Null()
    {
        var enc = new WireEncoder();
        enc.EncodeNull();
        var dec = new WireDecoder(enc.ToByteArray());
        var result = dec.Decode();
        Assert.True(result.IsNull);
    }

    [Fact]
    public void EncodeDecode_Int64_Zero()
    {
        var enc = new WireEncoder();
        enc.EncodeInt64(0);
        var dec = new WireDecoder(enc.ToByteArray());
        Assert.Equal(0, dec.Decode().AsInt64());
    }

    [Fact]
    public void EncodeDecode_Int64_Positive()
    {
        var enc = new WireEncoder();
        enc.EncodeInt64(42);
        var dec = new WireDecoder(enc.ToByteArray());
        Assert.Equal(42, dec.Decode().AsInt64());
    }

    [Fact]
    public void EncodeDecode_Int64_Large()
    {
        var enc = new WireEncoder();
        enc.EncodeInt64(1234567890123L);
        var dec = new WireDecoder(enc.ToByteArray());
        Assert.Equal(1234567890123L, dec.Decode().AsInt64());
    }

    [Fact]
    public void EncodeDecode_Int64_MaxValue()
    {
        var enc = new WireEncoder();
        enc.EncodeInt64(long.MaxValue);
        var dec = new WireDecoder(enc.ToByteArray());
        Assert.Equal(long.MaxValue, dec.Decode().AsInt64());
    }

    [Fact]
    public void EncodeDecode_Int64_MinValue()
    {
        var enc = new WireEncoder();
        enc.EncodeInt64(long.MinValue);
        var dec = new WireDecoder(enc.ToByteArray());
        Assert.Equal(long.MinValue, dec.Decode().AsInt64());
    }

    [Fact]
    public void EncodeDecode_Int64_Negative()
    {
        var enc = new WireEncoder();
        enc.EncodeInt64(-42);
        var dec = new WireDecoder(enc.ToByteArray());
        Assert.Equal(-42, dec.Decode().AsInt64());
    }

    [Fact]
    public void EncodeDecode_String()
    {
        var enc = new WireEncoder();
        enc.EncodeString("hello");
        var dec = new WireDecoder(enc.ToByteArray());
        Assert.Equal("hello", dec.Decode().AsString());
    }

    [Fact]
    public void EncodeDecode_String_Empty()
    {
        var enc = new WireEncoder();
        enc.EncodeString("");
        var dec = new WireDecoder(enc.ToByteArray());
        Assert.Equal("", dec.Decode().AsString());
    }

    [Fact]
    public void EncodeDecode_String_Chinese()
    {
        var enc = new WireEncoder();
        enc.EncodeString("你好世界");
        var dec = new WireDecoder(enc.ToByteArray());
        Assert.Equal("你好世界", dec.Decode().AsString());
    }

    [Fact]
    public void EncodeDecode_Bytes()
    {
        var enc = new WireEncoder();
        enc.EncodeBytes([0x00, 0xFF, 0xAB, 0xCD]);
        var dec = new WireDecoder(enc.ToByteArray());
        Assert.Equal([0x00, 0xFF, 0xAB, 0xCD], dec.Decode().AsBytes());
    }

    [Fact]
    public void EncodeDecode_Float()
    {
        var enc = new WireEncoder();
        enc.EncodeFloat(3.14f);
        var dec = new WireDecoder(enc.ToByteArray());
        Assert.Equal(3.14f, (float)dec.Decode().AsDouble(), 4);
    }

    [Fact]
    public void EncodeDecode_Double()
    {
        var enc = new WireEncoder();
        enc.EncodeDouble(3.14159265358979);
        var dec = new WireDecoder(enc.ToByteArray());
        Assert.Equal(3.14159265358979, dec.Decode().AsDouble(), 10);
    }

    [Fact]
    public void EncodeDecode_Array()
    {
        var enc = new WireEncoder();
        var itemEnc = new WireEncoder();
        itemEnc.EncodeInt64(1);
        itemEnc.EncodeInt64(2);
        itemEnc.EncodeInt64(3);
        enc.EncodeArrayPayload(itemEnc.ToByteArray());

        var dec = new WireDecoder(enc.ToByteArray());
        var arr = dec.Decode().AsArray();
        Assert.NotNull(arr);
        Assert.Equal(3, arr!.Length);
        Assert.Equal(1, arr[0].AsInt64());
        Assert.Equal(2, arr[1].AsInt64());
        Assert.Equal(3, arr[2].AsInt64());
    }

    [Fact]
    public void EncodeDecode_Map()
    {
        var enc = new WireEncoder();
        var itemEnc = new WireEncoder();
        itemEnc.EncodeString("name");
        itemEnc.EncodeString("Alice");
        itemEnc.EncodeString("age");
        itemEnc.EncodeInt64(30);
        enc.EncodeObjectPayload(itemEnc.ToByteArray());

        var dec = new WireDecoder(enc.ToByteArray());
        var map = dec.Decode().AsMap();
        Assert.NotNull(map);
        Assert.Equal(2, map!.Length);
        Assert.Equal("name", map[0].Key);
        Assert.Equal("Alice", map[0].Value.AsString());
        Assert.Equal("age", map[1].Key);
        Assert.Equal(30, map[1].Value.AsInt64());
    }

    [Fact]
    public void EncodeDecode_Tagged()
    {
        var enc = new WireEncoder();
        var payloadEnc = new WireEncoder();
        payloadEnc.EncodeString("test");
        var tag = Tag.Parse("type=str; desc=测试");
        enc.EncodeTaggedPayload(payloadEnc.ToByteArray(), tag.ToBytes());

        var dec = new WireDecoder(enc.ToByteArray());
        var result = dec.Decode();
        Assert.Equal("test", result.AsString());
        Assert.NotNull(result.RawTagBytes);
        var parsedTag = Tag.FromBytes(result.RawTagBytes!);
        Assert.Equal("测试", parsedTag.Desc);
    }

    [Fact]
    public void MetaMessageFacade_EncodeDecode_Object()
    {
        var user = new { name = "Alice", age = 30, active = true };
        byte[] data = MetaMessage.Encode(user);
        var tree = MetaMessage.DecodeToTree(data);
        Assert.NotNull(tree);
    }

    [Fact]
    public void Tag_Parse_Roundtrip()
    {
        string tagStr = "type=str; desc=用户名; min=1; max=50; pattern=^[a-zA-Z]+$; enums=admin|user|guest; nullable";
        var tag = Tag.Parse(tagStr);
        Assert.Equal(MmValueType.Str, tag.Type);
        Assert.Equal("用户名", tag.Desc);
        Assert.Equal("1", tag.Min);
        Assert.Equal("50", tag.Max);
        Assert.Equal("^[a-zA-Z]+$", tag.Pattern);
        Assert.Equal("admin|user|guest", tag.Enums);
        Assert.True(tag.Nullable);
    }

    [Fact]
    public void Tag_ToBytes_FromBytes_Roundtrip()
    {
        var tag = Tag.Parse("type=i; desc=数量; min=0; max=100; nullable");
        byte[] bytes = tag.ToBytes();
        var restored = Tag.FromBytes(bytes);
        Assert.Equal(tag.Type, restored.Type);
        Assert.Equal(tag.Desc, restored.Desc);
        Assert.Equal(tag.Min, restored.Min);
        Assert.Equal(tag.Max, restored.Max);
        Assert.Equal(tag.Nullable, restored.Nullable);
    }

    [Fact]
    public void Validator_Validates_MinMax()
    {
        var valid = LTAI.Mm.MetaMessage.Validate(50, "type=i; min=0; max=100");
        Assert.True(valid.IsValid);

        var invalid = LTAI.Mm.MetaMessage.Validate(-1, "type=i; min=0; max=100");
        Assert.False(invalid.IsValid);
    }

    [Fact]
    public void EncodeDecode_Int8()
    {
        var enc = new WireEncoder();
        enc.EncodeInt8(sbyte.MaxValue);
        var dec = new WireDecoder(enc.ToByteArray());
        Assert.Equal(sbyte.MaxValue, dec.Decode().AsInt64());
    }

    [Fact]
    public void EncodeDecode_UInt64()
    {
        var enc = new WireEncoder();
        enc.EncodeUInt64(ulong.MaxValue);
        var dec = new WireDecoder(enc.ToByteArray());
        var result = dec.Decode();
        Assert.Equal(ulong.MaxValue, Assert.IsType<ulong>(result.AsObject()));
    }

    [Fact]
    public void EncodeDecode_UInt64_Small()
    {
        var enc = new WireEncoder();
        enc.EncodeUInt64(42);
        var dec = new WireDecoder(enc.ToByteArray());
        var result = dec.Decode();
        Assert.Equal(42L, result.AsInt64());
    }
}
