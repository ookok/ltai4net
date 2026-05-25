using System.Text.RegularExpressions;

namespace LTAI.Agent.Skills.Runtime;

/// <summary>
/// Unified runtime value for the Skill DSL. Every value in the system
/// is a SkillValue — no boxing, no object wrapping.
/// </summary>
public readonly struct SkillValue
{
    private readonly string? _text;
    private readonly double _number;
    private readonly List<SkillValue>? _list;
    private readonly Dictionary<string, SkillValue>? _map;
    private readonly bool _boolean;
    private readonly SkillValueType _type;

    private enum SkillValueType { Null, String, Number, Bool, List, Map }

    public static readonly SkillValue Nil = default;

    private SkillValue(SkillValueType type, string? text = null, double num = 0, bool b = false,
        List<SkillValue>? list = null, Dictionary<string, SkillValue>? map = null)
    {
        _type = type; _text = text; _number = num; _boolean = b; _list = list; _map = map;
    }

    public static SkillValue FromString(string s) => new(SkillValueType.String, text: s);
    public static SkillValue FromNumber(double n) => new(SkillValueType.Number, num: n);
    public static SkillValue FromBool(bool b) => new(SkillValueType.Bool, b: b);
    public static SkillValue FromList(List<SkillValue> l) => new(SkillValueType.List, list: l);
    public static SkillValue FromMap(Dictionary<string, SkillValue> m) => new(SkillValueType.Map, map: m);

    public bool IsNull => _type == SkillValueType.Null;
    public bool IsString => _type == SkillValueType.String;
    public bool IsNumber => _type == SkillValueType.Number;
    public bool IsBool => _type == SkillValueType.Bool;
    public bool IsList => _type == SkillValueType.List;
    public bool IsMap => _type == SkillValueType.Map;

    public string Text => _text ?? ToString();
    public double Number => _number;
    public bool Bool => _boolean;
    public List<SkillValue> List => _list ?? new List<SkillValue>();
    public Dictionary<string, SkillValue> Map => _map ?? new Dictionary<string, SkillValue>();
    public int Count => _list?.Count ?? _map?.Count ?? (_text?.Length ?? 0);

    public SkillValue this[int index]
    {
        get => _list != null && index >= 0 && index < _list.Count ? _list[index] : Nil;
    }

    public SkillValue this[string key]
    {
        get => _map != null && _map.TryGetValue(key, out var v) ? v : Nil;
    }

    public static SkillValue operator +(SkillValue a, SkillValue b)
    {
        if (a.IsNumber && b.IsNumber) return FromNumber(a._number + b._number);
        return FromString(a.ToString() + b.ToString());
    }

    public static SkillValue operator -(SkillValue a, SkillValue b) =>
        FromNumber((a.IsNumber ? a._number : 0) - (b.IsNumber ? b._number : 0));

    public static SkillValue operator *(SkillValue a, SkillValue b) =>
        FromNumber((a.IsNumber ? a._number : 0) * (b.IsNumber ? b._number : 1));

    public static SkillValue operator /(SkillValue a, SkillValue b) =>
        FromNumber(b._number != 0 ? (a.IsNumber ? a._number : 0) / b._number : 0);

    public static SkillValue operator >(SkillValue a, SkillValue b) =>
        FromBool((a.IsNumber && b.IsNumber ? a._number > b._number : string.CompareOrdinal(a.ToString(), b.ToString()) > 0));

    public static SkillValue operator <(SkillValue a, SkillValue b) =>
        FromBool((a.IsNumber && b.IsNumber ? a._number < b._number : string.CompareOrdinal(a.ToString(), b.ToString()) < 0));

    public static SkillValue operator ==(SkillValue a, SkillValue b) =>
        FromBool(a._type == b._type && a._text == b._text && Math.Abs(a._number - b._number) < 0.0001);

    public static SkillValue operator !=(SkillValue a, SkillValue b) =>
        FromBool(!(a == b).Bool);

    public override string ToString()
    {
        return _type switch
        {
            SkillValueType.Null => "",
            SkillValueType.String => _text ?? "",
            SkillValueType.Number => _number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SkillValueType.Bool => _boolean ? "true" : "false",
            SkillValueType.List => $"[{string.Join(", ", _list!.Select(v => v.ToString()))}]",
            SkillValueType.Map => $"{{{string.Join(", ", _map!.Select(kv => $"{kv.Key}: {kv.Value}"))}}}",
            _ => ""
        };
    }

    public override bool Equals(object? obj) => obj is SkillValue v && (this == v).Bool;
    public override int GetHashCode() => HashCode.Combine(_type, _text, _number, _boolean);
}
