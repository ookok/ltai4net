using System.Reflection;
using LTAI.Mm.Core;
using LTAI.Mm.Ir;
using LTAI.Mm.Tree;

namespace LTAI.Mm.Reflection;

public static class ReflectBinder
{
    public static T Decode<T>(byte[] data)
    {
        var decoder = new WireDecoder(data);
        var result = decoder.Decode();
        var targetType = typeof(T);

        if (targetType.IsClass && targetType != typeof(string) && !targetType.IsArray && !typeof(System.Collections.IDictionary).IsAssignableFrom(targetType))
        {
            var map = result.AsMap();
            if (map != null)
            {
                var instance = Activator.CreateInstance<T>()!;
                ApplyMap(instance, map, targetType);
                return instance;
            }
        }

        return (T)ConvertResult(result, targetType)!;
    }

    private static void ApplyMap(object target, (string Key, WireDecoder.IDecodeResult Value)[] map, Type type)
    {
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var (key, value) in map)
        {
            var prop = FindProperty(props, key) ?? FindProperty(props, ToPascalCase(key));
            if (prop != null && prop.CanWrite)
            {
                var converted = ConvertResult(value, prop.PropertyType);
                prop.SetValue(target, converted);
            }
        }
    }

    public static void Bind(byte[] data, object target)
    {
        var decoder = new WireDecoder(data);
        var result = decoder.Decode();
        ApplyResult(result, target);
    }

    public static object? ConvertResult(WireDecoder.IDecodeResult result, Type targetType)
    {
        if (result.IsNull) return null;

        targetType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (result.ValueKind == MmValueType.U64)
        {
            if (targetType == typeof(ulong)) return (ulong)result.AsObject()!;
            return result.AsObject();
        }

        if (targetType == typeof(int)) return (int)result.AsInt64();
        if (targetType == typeof(long)) return result.AsInt64();
        if (targetType == typeof(short)) return (short)result.AsInt64();
        if (targetType == typeof(byte)) return (byte)result.AsInt64();
        if (targetType == typeof(uint)) return (uint)result.AsInt64();
        if (targetType == typeof(ulong)) return (ulong)result.AsInt64();
        if (targetType == typeof(sbyte)) return (sbyte)result.AsInt64();
        if (targetType == typeof(ushort)) return (ushort)result.AsInt64();
        if (targetType == typeof(bool)) return result.AsBool();
        if (targetType == typeof(double)) return result.AsDouble();
        if (targetType == typeof(float)) return (float)result.AsDouble();
        if (targetType == typeof(decimal)) return decimal.Parse(result.AsString() ?? "0");
        if (targetType == typeof(string)) return result.AsString();
        if (targetType == typeof(byte[])) return result.AsBytes();
        if (targetType == typeof(DateTime)) return DateTimeOffset.FromUnixTimeSeconds(result.AsInt64()).DateTime;
        if (targetType == typeof(Guid)) return Guid.Parse(result.AsString());
        if (targetType == typeof(object)) return result.AsObject();

        if (targetType.IsEnum) return Enum.Parse(targetType, result.AsString());

        if (targetType.IsGenericType)
        {
            var def = targetType.GetGenericTypeDefinition();
            if (def == typeof(List<>))
            {
                var elemType = targetType.GetGenericArguments()[0];
                var list = (System.Collections.IList)Activator.CreateInstance(targetType)!;
                if (result.AsArray() is { } arr)
                    foreach (var item in arr)
                        list.Add(ConvertResult(item, elemType));
                return list;
            }
            if (def == typeof(Dictionary<,>))
            {
                var args = targetType.GetGenericArguments();
                var dict = (System.Collections.IDictionary)Activator.CreateInstance(targetType)!;
                if (result.AsMap() is { } map)
                    foreach (var (key, value) in map)
                        dict[key] = ConvertResult(value, args[1]);
                return dict;
            }
        }

        if (targetType.IsArray)
        {
            var elemType = targetType.GetElementType()!;
            if (result.AsArray() is { } arr)
            {
                var array = Array.CreateInstance(elemType, arr.Length);
                for (int i = 0; i < arr.Length; i++)
                    array.SetValue(ConvertResult(arr[i], elemType), i);
                return array;
            }
            return Array.CreateInstance(elemType, 0);
        }

        return result.AsObject();
    }

    public static void ApplyResult(WireDecoder.IDecodeResult result, object target)
    {
        if (result.AsMap() is not { } map) return;

        var type = target.GetType();
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var (key, value) in map)
        {
            var prop = FindProperty(props, key) ?? FindProperty(props, ToPascalCase(key));
            if (prop != null && prop.CanWrite)
            {
                var converted = ConvertResult(value, prop.PropertyType);
                prop.SetValue(target, converted);
            }
        }
    }

    public static INode ResultToNode(WireDecoder.IDecodeResult result)
    {
        if (result.IsNull)
            return new NodeScalar(null, MmValueType.Unknown, "null")
            {
                Tag = result.RawTagBytes != null ? Tag.FromBytes(result.RawTagBytes) : null
            };

        var tag = result.RawTagBytes != null ? Tag.FromBytes(result.RawTagBytes) : null;

        if (result.AsArray() is { } arr)
        {
            var node = new MmArray { Tag = tag };
            foreach (var item in arr) node.Children.Add(ResultToNode(item));
            return node;
        }

        if (result.AsMap() is { } map)
        {
            var node = new MmMap { Tag = tag };
            foreach (var (key, value) in map)
                node.Entries.Add(new MmMapEntry(
                    (NodeScalar)ResultToNode(new MockStringResult(key)),
                    ResultToNode(value)));
            return node;
        }

        string text = result.ValueKind switch
        {
            MmValueType.Str or MmValueType.Email or MmValueType.Url or MmValueType.Ip or
            MmValueType.Uuid or MmValueType.Bytes or MmValueType.Enums or MmValueType.Media =>
                result.AsString(),
            _ => result.AsObject()?.ToString() ?? "null",
        };
        return new NodeScalar(result.AsObject(), result.ValueKind, text, tag);
    }

    private static PropertyInfo? FindProperty(PropertyInfo[] props, string name)
    {
        return Array.Find(props, p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    private sealed class MockStringResult : WireDecoder.IDecodeResult
    {
        private readonly string _value;
        public MockStringResult(string value) => _value = value;
        public object? AsObject() => _value;
        public long AsInt64() => throw new NotSupportedException();
        public string AsString() => _value;
        public bool AsBool() => throw new NotSupportedException();
        public double AsDouble() => throw new NotSupportedException();
        public byte[] AsBytes() => throw new NotSupportedException();
        public bool IsNull => false;
        public MmValueType ValueKind => MmValueType.Str;
        public byte[]? RawTagBytes => null;
        public WireDecoder.IDecodeResult[]? AsArray() => null;
        public (string Key, WireDecoder.IDecodeResult Value)[]? AsMap() => null;
    }
}
