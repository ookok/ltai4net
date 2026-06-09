using System.Globalization;
using LTAI.Mm.Core;
using LTAI.Mm.Ir;
using LTAI.Mm.Tree;

namespace LTAI.Mm.Jsonc;

public static class JsoncParser
{
    public static INode Parse(string jsonc)
    {
        var ctx = new ParserContext(jsonc);
        var result = ParseValue(ctx);
        return ToNode(result, null);
    }

    public static INode ParseFromBytes(byte[] data)
    {
        return Parse(System.Text.Encoding.UTF8.GetString(data));
    }

    public static T Bind<T>(string jsonc) where T : new()
    {
        var node = Parse(jsonc);
        var instance = new T();
        BindToObject(node, instance);
        return instance;
    }

    public static void Bind(string jsonc, object target)
    {
        var node = Parse(jsonc);
        BindToObject(node, target);
    }

    private sealed class ParserContext(string text)
    {
        public string Text { get; } = text;
        public int Pos { get; set; }
        public string? PendingTag { get; set; }

        public char Peek() => Pos < Text.Length ? Text[Pos] : '\0';
        public char Next() => Pos < Text.Length ? Text[Pos++] : '\0';

        public void SkipWhitespace()
        {
            while (Pos < Text.Length)
            {
                char c = Text[Pos];
                if (c == '/' && Pos + 1 < Text.Length)
                {
                    if (Text[Pos + 1] == '/')
                    {
                        int start = Pos;
                        Pos += 2;
                        int commentStart = Pos;
                        while (Pos < Text.Length && Text[Pos] != '\n') Pos++;
                        string comment = Text[commentStart..Pos].Trim();
                        if (comment.StartsWith("mm:"))
                        {
                            PendingTag = comment[3..].Trim();
                        }
                        continue;
                    }
                    if (Text[Pos + 1] == '*')
                    {
                        Pos += 2;
                        while (Pos + 1 < Text.Length && !(Text[Pos] == '*' && Text[Pos + 1] == '/')) Pos++;
                        if (Pos + 1 < Text.Length) Pos += 2;
                        continue;
                    }
                }
                if (!char.IsWhiteSpace(c)) break;
                Pos++;
            }
        }
    }

    private sealed class JsonValue
    {
        public enum Kind { Null, Bool, Number, String, Array, Object }
        public Kind Type { get; set; }
        public bool BoolValue { get; set; }
        public string? StringValue { get; set; }
        public double NumberValue { get; set; }
        public List<JsonValue>? ArrayItems { get; set; }
        public List<(string Key, JsonValue Value, string? Tag)>? ObjectEntries { get; set; }
        public string? Tag { get; set; }
    }

    private static JsonValue ParseValue(ParserContext ctx)
    {
        ctx.SkipWhitespace();
        char c = ctx.Peek();
        var tag = ctx.PendingTag;
        ctx.PendingTag = null;

        JsonValue result = c switch
        {
            '"' => ParseString(ctx),
            '{' => ParseObject(ctx),
            '[' => ParseArray(ctx),
            't' or 'f' => ParseBool(ctx),
            'n' => ParseNull(ctx),
            '-' or '0' or '1' or '2' or '3' or '4' or '5' or '6' or '7' or '8' or '9' => ParseNumber(ctx),
            _ => throw new FormatException($"Unexpected character '{c}' at position {ctx.Pos}"),
        };
        result.Tag = tag;
        return result;
    }

    private static JsonValue ParseString(ParserContext ctx)
    {
        ctx.Next(); // skip opening "
        var sb = new System.Text.StringBuilder();
        while (ctx.Pos < ctx.Text.Length)
        {
            char c = ctx.Next();
            if (c == '"') break;
            if (c == '\\')
            {
                char esc = ctx.Next();
                sb.Append(esc switch
                {
                    '"' => '"', '\\' => '\\', '/' => '/',
                    'n' => '\n', 'r' => '\r', 't' => '\t',
                    'u' => (char)int.Parse(ctx.Text[ctx.Pos..(ctx.Pos + 4)], NumberStyles.HexNumber),
                    _ => esc,
                });
                if (esc == 'u') ctx.Pos += 4;
            }
            else
            {
                sb.Append(c);
            }
        }
        return new JsonValue { Type = JsonValue.Kind.String, StringValue = sb.ToString() };
    }

    private static JsonValue ParseNumber(ParserContext ctx)
    {
        int start = ctx.Pos;
        while (ctx.Pos < ctx.Text.Length && "-0123456789.eE+".Contains(ctx.Peek()))
            ctx.Pos++;
        string numStr = ctx.Text[start..ctx.Pos];
        bool isFloat = numStr.Contains('.') || numStr.Contains('e') || numStr.Contains('E');
        return new JsonValue
        {
            Type = JsonValue.Kind.Number,
            NumberValue = double.Parse(numStr, CultureInfo.InvariantCulture),
            StringValue = numStr,
        };
    }

    private static JsonValue ParseBool(ParserContext ctx)
    {
        if (ctx.Text[ctx.Pos..].StartsWith("true")) { ctx.Pos += 4; return new JsonValue { Type = JsonValue.Kind.Bool, BoolValue = true }; }
        ctx.Pos += 5; return new JsonValue { Type = JsonValue.Kind.Bool, BoolValue = false };
    }

    private static JsonValue ParseNull(ParserContext ctx)
    {
        ctx.Pos += 4;
        return new JsonValue { Type = JsonValue.Kind.Null };
    }

    private static JsonValue ParseArray(ParserContext ctx)
    {
        ctx.Next(); // skip [
        var items = new List<JsonValue>();
        while (ctx.Peek() != ']')
        {
            items.Add(ParseValue(ctx));
            ctx.SkipWhitespace();
            if (ctx.Peek() == ',') { ctx.Next(); ctx.SkipWhitespace(); }
        }
        ctx.Next(); // skip ]
        return new JsonValue { Type = JsonValue.Kind.Array, ArrayItems = items };
    }

    private static JsonValue ParseObject(ParserContext ctx)
    {
        ctx.Next(); // skip {
        var entries = new List<(string, JsonValue, string?)>();
        while (ctx.Peek() != '}')
        {
            ctx.SkipWhitespace();
            var tag = ctx.PendingTag;
            ctx.PendingTag = null;

            var key = ParseString(ctx);
            ctx.SkipWhitespace();
            if (ctx.Next() != ':') throw new FormatException("Expected ':' in object");
            ctx.SkipWhitespace();
            var value = ParseValue(ctx);

            entries.Add((key.StringValue ?? "", value, tag));
            ctx.SkipWhitespace();
            if (ctx.Peek() == ',') { ctx.Next(); }
        }
        ctx.Next(); // skip }
        return new JsonValue { Type = JsonValue.Kind.Object, ObjectEntries = entries };
    }

    private static INode ToNode(JsonValue val, Tag? parentTag)
    {
        Tag? tag = val.Tag != null ? Tag.Parse(val.Tag) : parentTag;

        switch (val.Type)
        {
            case JsonValue.Kind.Null:
                return new NodeScalar(null, MmValueType.Unknown, "null") { Tag = tag };

            case JsonValue.Kind.Bool:
                return new NodeScalar(val.BoolValue, MmValueType.Bool, val.BoolValue ? "true" : "false") { Tag = tag };

            case JsonValue.Kind.Number:
                bool isFloat = val.StringValue?.Contains('.') == true || val.StringValue?.Contains('e') == true;
                if (isFloat)
                    return new NodeScalar(val.NumberValue, MmValueType.F64, val.StringValue!) { Tag = tag };
                return new NodeScalar((long)val.NumberValue, MmValueType.I64, val.StringValue!) { Tag = tag };

            case JsonValue.Kind.String:
                return new NodeScalar(val.StringValue, MmValueType.Str, val.StringValue ?? "") { Tag = tag };

            case JsonValue.Kind.Array:
                var arr = new MmArray { Tag = tag };
                foreach (var item in val.ArrayItems ?? [])
                    arr.Children.Add(ToNode(item, null));
                return arr;

            case JsonValue.Kind.Object:
                var map = new MmMap { Tag = tag };
                if (val.ObjectEntries != null)
                {
                    foreach (var (key, value, entryTag) in val.ObjectEntries)
                    {
                        var entryTagObj = entryTag != null ? Tag.Parse(entryTag) : null;
                        map.Entries.Add(new MmMapEntry(
                            new NodeScalar(key, MmValueType.Str, key),
                            ToNode(value, entryTagObj)));
                    }
                }
                return map;

            default:
                return new NodeScalar(null, MmValueType.Unknown, "null");
        }
    }

    private static void BindToObject(INode node, object target)
    {
        if (node is not MmMap map) return;
        var type = target.GetType();
        var props = type.GetProperties(
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance);

        foreach (var entry in map.Entries)
        {
            var prop = Array.Find(props, p =>
                p.Name.Equals(entry.Key.Text, StringComparison.OrdinalIgnoreCase));
            if (prop == null || !prop.CanWrite) continue;

            object? value = ExtractScalar(entry.Value, prop.PropertyType);
            if (value != null)
                prop.SetValue(target, value);
        }
    }

    private static object? ExtractScalar(INode node, Type targetType)
    {
        if (node is NodeScalar scalar)
        {
            if (scalar.Data == null) return null;
            if (targetType == typeof(string)) return scalar.Text;
            if (targetType == typeof(int) || targetType == typeof(int?)) return (int)(long)scalar.Data;
            if (targetType == typeof(long) || targetType == typeof(long?)) return (long)scalar.Data;
            if (targetType == typeof(double) || targetType == typeof(double?)) return (double)scalar.Data;
            if (targetType == typeof(float) || targetType == typeof(float?)) return (float)(double)scalar.Data;
            if (targetType == typeof(bool) || targetType == typeof(bool?)) return scalar.Data;
            if (targetType == typeof(byte) || targetType == typeof(byte?)) return (byte)(long)scalar.Data;
            if (targetType == typeof(short) || targetType == typeof(short?)) return (short)(long)scalar.Data;
            return Convert.ChangeType(scalar.Data, targetType);
        }
        return null;
    }
}
