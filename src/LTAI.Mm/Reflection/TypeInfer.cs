using LTAI.Mm.Core;

namespace LTAI.Mm.Reflection;

public static class TypeInfer
{
    public static MmValueType Infer(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type.IsEnum) return MmValueType.Enums;
        if (type == typeof(bool)) return MmValueType.Bool;
        if (type == typeof(byte)) return MmValueType.U8;
        if (type == typeof(sbyte)) return MmValueType.I8;
        if (type == typeof(short)) return MmValueType.I16;
        if (type == typeof(ushort)) return MmValueType.U16;
        if (type == typeof(int)) return MmValueType.I;
        if (type == typeof(uint)) return MmValueType.U;
        if (type == typeof(long)) return MmValueType.I64;
        if (type == typeof(ulong)) return MmValueType.U64;
        if (type == typeof(float)) return MmValueType.F32;
        if (type == typeof(double)) return MmValueType.F64;
        if (type == typeof(decimal)) return MmValueType.Decimal;
        if (type == typeof(string)) return MmValueType.Str;
        if (type == typeof(DateTime)) return MmValueType.DateTime;
        if (type == typeof(byte[])) return MmValueType.Bytes;
        if (type == typeof(Guid)) return MmValueType.Uuid;

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IEnumerable<>))
                return MmValueType.Vec;
            if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>))
                return MmValueType.Map;
        }

        if (type.IsArray) return MmValueType.Arr;
        if (type.IsClass || type.IsValueType) return MmValueType.Obj;

        return MmValueType.Unknown;
    }

    public static MmValueType InferFromProperty(
        System.Reflection.PropertyInfo prop,
        LTAI.Mm.Ir.Tag? parsedTag)
    {
        if (parsedTag?.Type != MmValueType.Unknown)
            return parsedTag?.Type ?? MmValueType.Unknown;
        return Infer(prop.PropertyType);
    }
}
