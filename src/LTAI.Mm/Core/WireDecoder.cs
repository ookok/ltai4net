namespace LTAI.Mm.Core;

public sealed class WireDecoder
{
    private readonly byte[] _data;
    private int _pos;

    public WireDecoder(byte[] data)
    {
        _data = data;
        _pos = 0;
    }

    public int Position => _pos;
    public int Remaining => _data.Length - _pos;
    public bool HasMore => _pos < _data.Length;

    public interface IDecodeResult
    {
        object? AsObject();
        long AsInt64();
        string AsString();
        bool AsBool();
        double AsDouble();
        byte[] AsBytes();
        bool IsNull { get; }
        MmValueType ValueKind { get; }
        byte[]? RawTagBytes { get; }
        IDecodeResult[]? AsArray();
        (string Key, IDecodeResult Value)[]? AsMap();
    }

    private sealed class DecodeResult : IDecodeResult
    {
        public object? Value;
        public MmValueType Kind;
        public byte[]? TagBytes;

        public object? AsObject() => Value;
        public long AsInt64() => Value is long l ? l : throw new InvalidCastException();
        public string AsString() => Value is string s ? s : throw new InvalidCastException();
        public bool AsBool() => Value is bool b ? b : throw new InvalidCastException();
        public double AsDouble() => Value is double d ? d : throw new InvalidCastException();
        public byte[] AsBytes() => Value is byte[] ba ? ba : throw new InvalidCastException();
        public bool IsNull => Value is null && Kind == MmValueType.Unknown;
        public MmValueType ValueKind => Kind;
        public byte[]? RawTagBytes => TagBytes;
        public IDecodeResult[]? AsArray() => Value is IDecodeResult[] arr ? arr : null;
        public (string Key, IDecodeResult Value)[]? AsMap()
        {
            if (Value is (string, IDecodeResult)[] m) return m;
            if (Value is System.Collections.IEnumerable entries)
            {
                var list = new List<(string, IDecodeResult)>();
                foreach (var entry in entries)
                {
                    if (entry is System.Collections.DictionaryEntry de)
                    {
                        var keyStr = de.Key?.ToString() ?? "";
                        if (de.Value is IDecodeResult idr)
                            list.Add((keyStr, idr));
                    }
                }
                return list.Count > 0 ? list.ToArray() : null;
            }
            return null;
        }
    }

    public IDecodeResult Decode()
    {
        if (_pos >= _data.Length)
            throw new InvalidOperationException("Unexpected end of data");

        int header = _data[_pos++];
        int prefix = header & Prefix.MASK;
        int lenInfo = header & Prefix.LEN_MASK;

        return prefix switch
        {
            Prefix.SIMPLE => DecodeSimple(lenInfo),
            Prefix.POSITIVE_INT => DecodePositiveInt(lenInfo),
            Prefix.NEGATIVE_INT => DecodeNegativeInt(lenInfo),
            Prefix.FLOAT => DecodeFloat(header),
            Prefix.STRING => DecodeString(lenInfo),
            Prefix.BYTES => DecodeBytes(lenInfo),
            Prefix.CONTAINER => DecodeContainer(header),
            Prefix.TAG => DecodeTagged(header),
            _ => throw new InvalidOperationException($"Unknown prefix: 0x{prefix:X2}"),
        };
    }

    private IDecodeResult DecodeSimple(int lenInfo)
    {
        var result = new DecodeResult { Kind = MmValueType.Bool };
        if (lenInfo == SimpleValue.TRUE) result.Value = true;
        else if (lenInfo == SimpleValue.FALSE) result.Value = false;
        else if (lenInfo == SimpleValue.NULL)
        {
            result.Value = null;
            result.Kind = MmValueType.Unknown;
        }
        else
        {
            result.Value = (long)lenInfo;
            result.Kind = MmValueType.I;
        }
        return result;
    }

    private IDecodeResult DecodePositiveInt(int lenInfo)
    {
        var result = new DecodeResult { Kind = MmValueType.I64 };
        if (lenInfo <= WireConstants.INT_LEN_1)
        {
            result.Value = (long)_data[_pos++];
        }
        else if (lenInfo == WireConstants.INT_LEN_2)
            result.Value = (long)(_data[_pos++] << 8 | _data[_pos++]);
        else if (lenInfo == WireConstants.INT_LEN_3)
            result.Value = (long)(_data[_pos++] << 16 | _data[_pos++] << 8 | _data[_pos++]);
        else if (lenInfo == WireConstants.INT_LEN_4)
            result.Value = (long)((uint)(_data[_pos++] << 24 | _data[_pos++] << 16 | _data[_pos++] << 8 | _data[_pos++]));
        else if (lenInfo == WireConstants.INT_LEN_5)
            result.Value = (long)((ulong)_data[_pos++] << 32 | (ulong)_data[_pos++] << 24 | (ulong)_data[_pos++] << 16 | (ulong)_data[_pos++] << 8 | _data[_pos++]);
        else if (lenInfo == WireConstants.INT_LEN_6)
            result.Value = (long)((ulong)_data[_pos++] << 40 | (ulong)_data[_pos++] << 32 | (ulong)_data[_pos++] << 24 | (ulong)_data[_pos++] << 16 | (ulong)_data[_pos++] << 8 | _data[_pos++]);
        else if (lenInfo == WireConstants.INT_LEN_7)
            result.Value = (long)((ulong)_data[_pos++] << 48 | (ulong)_data[_pos++] << 40 | (ulong)_data[_pos++] << 32 | (ulong)_data[_pos++] << 24 | (ulong)_data[_pos++] << 16 | (ulong)_data[_pos++] << 8 | _data[_pos++]);
        else
        {
            ulong uv = (ulong)_data[_pos++] << 56 | (ulong)_data[_pos++] << 48 | (ulong)_data[_pos++] << 40 | (ulong)_data[_pos++] << 32 | (ulong)_data[_pos++] << 24 | (ulong)_data[_pos++] << 16 | (ulong)_data[_pos++] << 8 | _data[_pos++];
            if (uv > (ulong)long.MaxValue)
            {
                result.Kind = MmValueType.U64;
                result.Value = uv;
            }
            else
            {
                result.Value = (long)uv;
            }
        }
        return result;
    }

    private IDecodeResult DecodeNegativeInt(int lenInfo)
    {
        var result = new DecodeResult { Kind = MmValueType.I64 };
        ulong uv;
        if (lenInfo <= WireConstants.INT_LEN_1)
        {
            uv = _data[_pos++];
        }
        else if (lenInfo == WireConstants.INT_LEN_2)
            uv = (ulong)(_data[_pos++] << 8 | _data[_pos++]);
        else if (lenInfo == WireConstants.INT_LEN_3)
            uv = (ulong)(_data[_pos++] << 16 | _data[_pos++] << 8 | _data[_pos++]);
        else if (lenInfo == WireConstants.INT_LEN_4)
            uv = (uint)(_data[_pos++] << 24 | _data[_pos++] << 16 | _data[_pos++] << 8 | _data[_pos++]);
        else if (lenInfo == WireConstants.INT_LEN_5)
            uv = (ulong)_data[_pos++] << 32 | (ulong)_data[_pos++] << 24 | (ulong)_data[_pos++] << 16 | (ulong)_data[_pos++] << 8 | _data[_pos++];
        else if (lenInfo == WireConstants.INT_LEN_6)
            uv = (ulong)_data[_pos++] << 40 | (ulong)_data[_pos++] << 32 | (ulong)_data[_pos++] << 24 | (ulong)_data[_pos++] << 16 | (ulong)_data[_pos++] << 8 | _data[_pos++];
        else if (lenInfo == WireConstants.INT_LEN_7)
            uv = (ulong)_data[_pos++] << 48 | (ulong)_data[_pos++] << 40 | (ulong)_data[_pos++] << 32 | (ulong)_data[_pos++] << 24 | (ulong)_data[_pos++] << 16 | (ulong)_data[_pos++] << 8 | _data[_pos++];
        else
            uv = (ulong)_data[_pos++] << 56 | (ulong)_data[_pos++] << 48 | (ulong)_data[_pos++] << 40 | (ulong)_data[_pos++] << 32 | (ulong)_data[_pos++] << 24 | (ulong)_data[_pos++] << 16 | (ulong)_data[_pos++] << 8 | _data[_pos++];

        if (uv == 9223372036854775808UL)
            result.Value = long.MinValue;
        else
            result.Value = -(long)uv;
        return result;
    }

    private IDecodeResult DecodeFloat(int header)
    {
        var result = new DecodeResult { Kind = MmValueType.F64 };
        int sign = header & Prefix.LEN_MASK;
        bool negative = (sign & WireConstants.FLOAT_NEG_MASK) != 0;
        int lenInfo = sign & 0x07;

        sbyte exponent = (sbyte)_data[_pos++];
        ulong mantissa;
        if (lenInfo == WireConstants.FLOAT_LEN_1)
            mantissa = _data[_pos++];
        else if (lenInfo == WireConstants.FLOAT_LEN_2)
            mantissa = (ulong)(_data[_pos++] << 8 | _data[_pos++]);
        else if (lenInfo == WireConstants.FLOAT_LEN_3)
            mantissa = (ulong)(_data[_pos++] << 16 | _data[_pos++] << 8 | _data[_pos++]);
        else if (lenInfo == WireConstants.FLOAT_LEN_4)
            mantissa = (uint)(_data[_pos++] << 24 | _data[_pos++] << 16 | _data[_pos++] << 8 | _data[_pos++]);
        else if (lenInfo == WireConstants.FLOAT_LEN_5)
            mantissa = (ulong)_data[_pos++] << 32 | (ulong)_data[_pos++] << 24 | (ulong)_data[_pos++] << 16 | (ulong)_data[_pos++] << 8 | _data[_pos++];
        else if (lenInfo == WireConstants.FLOAT_LEN_6)
            mantissa = (ulong)_data[_pos++] << 40 | (ulong)_data[_pos++] << 32 | (ulong)_data[_pos++] << 24 | (ulong)_data[_pos++] << 16 | (ulong)_data[_pos++] << 8 | _data[_pos++];
        else if (lenInfo == WireConstants.FLOAT_LEN_7)
            mantissa = (ulong)_data[_pos++] << 48 | (ulong)_data[_pos++] << 40 | (ulong)_data[_pos++] << 32 | (ulong)_data[_pos++] << 24 | (ulong)_data[_pos++] << 16 | (ulong)_data[_pos++] << 8 | _data[_pos++];
        else
            mantissa = (ulong)_data[_pos++] << 56 | (ulong)_data[_pos++] << 48 | (ulong)_data[_pos++] << 40 | (ulong)_data[_pos++] << 32 | (ulong)_data[_pos++] << 24 | (ulong)_data[_pos++] << 16 | (ulong)_data[_pos++] << 8 | _data[_pos++];

        string s = FloatCodec.FormatDecimal(negative, exponent, mantissa);
        result.Value = double.Parse(s);
        return result;
    }

    private IDecodeResult DecodeString(int lenInfo)
    {
        var result = new DecodeResult { Kind = MmValueType.Str };
        int length;
        if (lenInfo > 0)
            length = lenInfo;
        else if (lenInfo == WireConstants.STRING_LEN_1)
            length = _data[_pos++];
        else if (lenInfo == WireConstants.STRING_LEN_2)
            length = _data[_pos++] << 8 | _data[_pos++];
        else
            throw new InvalidOperationException($"Invalid string length type: {lenInfo}");

        string s = System.Text.Encoding.UTF8.GetString(_data, _pos, length);
        _pos += length;
        result.Value = s;
        return result;
    }

    private IDecodeResult DecodeBytes(int lenInfo)
    {
        var result = new DecodeResult { Kind = MmValueType.Bytes };
        int length;
        if (lenInfo > 0)
            length = lenInfo;
        else if (lenInfo == WireConstants.BYTES_LEN_1)
            length = _data[_pos++];
        else if (lenInfo == WireConstants.BYTES_LEN_2)
            length = _data[_pos++] << 8 | _data[_pos++];
        else
            throw new InvalidOperationException($"Invalid bytes length type: {lenInfo}");

        var bytes = new byte[length];
        Buffer.BlockCopy(_data, _pos, bytes, 0, length);
        _pos += length;
        result.Value = bytes;
        return result;
    }

    private IDecodeResult DecodeContainer(int header)
    {
        int containerType = header & 0x08;
        int lenInfo = header & 0x07;

        int length;
        if (lenInfo == WireConstants.CONTAINER_LEN_1)
            length = _data[_pos++];
        else if (lenInfo == WireConstants.CONTAINER_LEN_2)
            length = _data[_pos++] << 8 | _data[_pos++];
        else
            length = lenInfo;

        int end = _pos + length;
        if (end > _data.Length)
            throw new InvalidOperationException("Container payload exceeds data length");

        if (containerType == WireConstants.CONTAINER_ARRAY)
        {
            var items = new List<IDecodeResult>();
            while (_pos < end)
                items.Add(Decode());
            var result = new DecodeResult { Kind = MmValueType.Vec };
            result.Value = items.ToArray();
            return result;
        }
        else
        {
            var entries = new List<(string, IDecodeResult)>();
            while (_pos < end)
            {
                var keyResult = Decode();
                string key = keyResult.AsString();
                var value = Decode();
                entries.Add((key, value));
            }
            var result = new DecodeResult { Kind = MmValueType.Obj };
            result.Value = entries.ToArray();
            return result;
        }
    }

    private IDecodeResult DecodeTagged(int header)
    {
        int totalLength = _data[_pos++];

        int end = _pos + totalLength;
        if (end > _data.Length)
            throw new InvalidOperationException("Tag payload exceeds data length");

        int tagByte = _data[_pos++];
        int tagLen;
        if (tagByte < 254)
            tagLen = tagByte;
        else if (tagByte == 254)
            tagLen = _data[_pos++];
        else
            tagLen = _data[_pos++] << 8 | _data[_pos++];

        byte[] tagBytes = new byte[tagLen];
        Buffer.BlockCopy(_data, _pos, tagBytes, 0, tagLen);
        _pos += tagLen;

        var innerResult = (DecodeResult)Decode();
        innerResult.TagBytes = tagBytes;
        return innerResult;
    }
}
