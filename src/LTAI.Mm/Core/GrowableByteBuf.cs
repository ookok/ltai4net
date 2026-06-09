namespace LTAI.Mm.Core;

internal sealed class GrowableByteBuf
{
    private byte[] _data;
    private int _length;

    internal GrowableByteBuf(int capacity = 256)
    {
        _data = new byte[capacity];
        _length = 0;
    }

    internal int Length => _length;
    internal void Reset() => _length = 0;

    internal byte[] ToArray()
    {
        var result = new byte[_length];
        Buffer.BlockCopy(_data, 0, result, 0, _length);
        return result;
    }

    internal void Write(int b)
    {
        Ensure(1);
        _data[_length++] = (byte)b;
    }

    internal void Write(int b1, byte b2)
    {
        Ensure(2);
        _data[_length++] = (byte)b1;
        _data[_length++] = b2;
    }

    internal void Write(int b1, byte b2, byte b3)
    {
        Ensure(3);
        _data[_length++] = (byte)b1;
        _data[_length++] = b2;
        _data[_length++] = b3;
    }

    internal void Write(int b1, byte b2, byte b3, byte b4)
    {
        Ensure(4);
        _data[_length++] = (byte)b1;
        _data[_length++] = b2;
        _data[_length++] = b3;
        _data[_length++] = b4;
    }

    internal void Write(int b1, byte b2, byte b3, byte b4, byte b5)
    {
        Ensure(5);
        _data[_length++] = (byte)b1;
        _data[_length++] = b2; _data[_length++] = b3;
        _data[_length++] = b4; _data[_length++] = b5;
    }

    internal void Write(int b1, byte b2, byte b3, byte b4, byte b5, byte b6)
    {
        Ensure(6);
        _data[_length++] = (byte)b1;
        _data[_length++] = b2; _data[_length++] = b3;
        _data[_length++] = b4; _data[_length++] = b5;
        _data[_length++] = b6;
    }

    internal void Write(int b1, byte b2, byte b3, byte b4, byte b5, byte b6, byte b7)
    {
        Ensure(7);
        _data[_length++] = (byte)b1;
        _data[_length++] = b2; _data[_length++] = b3;
        _data[_length++] = b4; _data[_length++] = b5;
        _data[_length++] = b6; _data[_length++] = b7;
    }

    internal void Write(int b1, byte b2, byte b3, byte b4, byte b5, byte b6, byte b7, byte b8)
    {
        Ensure(8);
        _data[_length++] = (byte)b1;
        _data[_length++] = b2; _data[_length++] = b3;
        _data[_length++] = b4; _data[_length++] = b5;
        _data[_length++] = b6; _data[_length++] = b7;
        _data[_length++] = b8;
    }

    internal void Write(int b1, byte b2, byte b3, byte b4, byte b5, byte b6, byte b7, byte b8, byte b9)
    {
        Ensure(9);
        _data[_length++] = (byte)b1;
        _data[_length++] = b2; _data[_length++] = b3;
        _data[_length++] = b4; _data[_length++] = b5;
        _data[_length++] = b6; _data[_length++] = b7;
        _data[_length++] = b8; _data[_length++] = b9;
    }

    internal void Write(int b1, byte b2, byte b3, byte b4, byte b5, byte b6, byte b7, byte b8, byte b9, byte b10)
    {
        Ensure(10);
        _data[_length++] = (byte)b1;
        _data[_length++] = b2; _data[_length++] = b3;
        _data[_length++] = b4; _data[_length++] = b5;
        _data[_length++] = b6; _data[_length++] = b7;
        _data[_length++] = b8; _data[_length++] = b9;
        _data[_length++] = b10;
    }

    internal void WriteWithBytes(int firstByte, byte[] bytes)
    {
        Ensure(1 + bytes.Length);
        _data[_length++] = (byte)firstByte;
        Buffer.BlockCopy(bytes, 0, _data, _length, bytes.Length);
        _length += bytes.Length;
    }

    internal void WriteWithBytes(int firstByte, byte b2, byte[] bytes)
    {
        Ensure(2 + bytes.Length);
        _data[_length++] = (byte)firstByte;
        _data[_length++] = b2;
        Buffer.BlockCopy(bytes, 0, _data, _length, bytes.Length);
        _length += bytes.Length;
    }

    internal void WriteWithBytes(int firstByte, byte b2, byte b3, byte[] bytes)
    {
        Ensure(3 + bytes.Length);
        _data[_length++] = (byte)firstByte;
        _data[_length++] = b2;
        _data[_length++] = b3;
        Buffer.BlockCopy(bytes, 0, _data, _length, bytes.Length);
        _length += bytes.Length;
    }

    internal void WriteWithMultipleBytes(int firstByte, byte[] b1, byte[] b2)
    {
        Ensure(1 + b1.Length + b2.Length);
        _data[_length++] = (byte)firstByte;
        Buffer.BlockCopy(b1, 0, _data, _length, b1.Length);
        _length += b1.Length;
        Buffer.BlockCopy(b2, 0, _data, _length, b2.Length);
        _length += b2.Length;
    }

    internal void WriteWithMultipleBytes(int firstByte, byte b2, byte[] b1, byte[] b2Arr)
    {
        Ensure(2 + b1.Length + b2Arr.Length);
        _data[_length++] = (byte)firstByte;
        _data[_length++] = b2;
        Buffer.BlockCopy(b1, 0, _data, _length, b1.Length);
        _length += b1.Length;
        Buffer.BlockCopy(b2Arr, 0, _data, _length, b2Arr.Length);
        _length += b2Arr.Length;
    }

    internal void WriteWithMultipleBytes(int firstByte, byte b2, byte b3, byte[] b1, byte[] b2Arr)
    {
        Ensure(3 + b1.Length + b2Arr.Length);
        _data[_length++] = (byte)firstByte;
        _data[_length++] = b2;
        _data[_length++] = b3;
        Buffer.BlockCopy(b1, 0, _data, _length, b1.Length);
        _length += b1.Length;
        Buffer.BlockCopy(b2Arr, 0, _data, _length, b2Arr.Length);
        _length += b2Arr.Length;
    }

    internal void WriteAll(byte[] data)
    {
        Ensure(data.Length);
        Buffer.BlockCopy(data, 0, _data, _length, data.Length);
        _length += data.Length;
    }

    private void Ensure(int needed)
    {
        int required = _length + needed;
        if (required > _data.Length)
        {
            int newSize = Math.Max(_data.Length * 2, required);
            Array.Resize(ref _data, newSize);
        }
    }
}
