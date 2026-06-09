using LTAI.Mm.Core;

namespace LTAI.Mm.Ir;

public sealed class Tag
{
    public MmValueType Type { get; set; } = MmValueType.Unknown;
    public string? Name { get; set; }
    public string? Desc { get; set; }
    public string? DefaultVal { get; set; }
    public string? Min { get; set; }
    public string? Max { get; set; }
    public string? Pattern { get; set; }
    public string? Enums { get; set; }
    public bool Nullable { get; set; }
    public bool IsNull { get; set; }
    public bool Example { get; set; }
    public bool Deprecated { get; set; }
    public bool AllowEmpty { get; set; }
    public bool Unique { get; set; }
    public string? Size { get; set; }

    public MmValueType ChildType { get; set; } = MmValueType.Unknown;
    public string? ChildDesc { get; set; }
    public string? ChildMin { get; set; }
    public string? ChildMax { get; set; }
    public string? ChildPattern { get; set; }
    public string? ChildEnums { get; set; }
    public bool ChildNullable { get; set; }
    public bool ChildAllowEmpty { get; set; }
    public bool ChildUnique { get; set; }

    public string? Location { get; set; }
    public string? Version { get; set; }
    public string? Mime { get; set; }
    public string? ChildLocation { get; set; }
    public string? ChildVersion { get; set; }
    public string? ChildMime { get; set; }

    public static Tag Parse(string tagString)
    {
        var tag = new Tag();
        if (string.IsNullOrWhiteSpace(tagString) || tagString == "-")
        {
            if (tagString == "-") tag.Deprecated = true;
            return tag;
        }

        var parts = SplitTagString(tagString);
        foreach (var part in parts)
        {
            int eq = part.IndexOf('=');
            string key, value;
            if (eq >= 0)
            {
                key = part[..eq].Trim().ToLowerInvariant();
                value = part[(eq + 1)..].Trim();
            }
            else
            {
                key = part.Trim().ToLowerInvariant();
                value = "";
            }

            switch (key)
            {
                case "type": tag.Type = MmValueTypeExtensions.FromTypeString(value); break;
                case "name": tag.Name = value; break;
                case "desc": tag.Desc = value; break;
                case "default": tag.DefaultVal = value; break;
                case "min": tag.Min = value; break;
                case "max": tag.Max = value; break;
                case "size": tag.Size = value; break;
                case "pattern": tag.Pattern = value; break;
                case "enums": tag.Enums = value; break;
                case "nullable" or "nillable": tag.Nullable = true; break;
                case "is_null": tag.IsNull = true; break;
                case "example": tag.Example = true; break;
                case "deprecated": tag.Deprecated = true; break;
                case "allow_empty": tag.AllowEmpty = true; break;
                case "unique": tag.Unique = true; break;
                case "location": tag.Location = value; break;
                case "version": tag.Version = value; break;
                case "mime": tag.Mime = value; break;
                case "child_type": tag.ChildType = MmValueTypeExtensions.FromTypeString(value); break;
                case "child_desc": tag.ChildDesc = value; break;
                case "child_min": tag.ChildMin = value; break;
                case "child_max": tag.ChildMax = value; break;
                case "child_pattern": tag.ChildPattern = value; break;
                case "child_enums": tag.ChildEnums = value; break;
                case "child_nullable" or "child_nillable": tag.ChildNullable = true; break;
                case "child_allow_empty": tag.ChildAllowEmpty = true; break;
                case "child_unique": tag.ChildUnique = true; break;
                case "child_location": tag.ChildLocation = value; break;
                case "child_version": tag.ChildVersion = value; break;
                case "child_mime": tag.ChildMime = value; break;
            }
        }
        return tag;
    }

    public string ToTagString()
    {
        var parts = new List<string>();
        if (Type != MmValueType.Unknown) parts.Add($"type={Type.ToTypeString()}");
        if (Name != null) parts.Add($"name={Name}");
        if (Desc != null) parts.Add($"desc={Desc}");
        if (DefaultVal != null) parts.Add($"default={DefaultVal}");
        if (Min != null) parts.Add($"min={Min}");
        if (Max != null) parts.Add($"max={Max}");
        if (Size != null) parts.Add($"size={Size}");
        if (Pattern != null) parts.Add($"pattern={Pattern}");
        if (Enums != null) parts.Add($"enums={Enums}");
        if (Nullable) parts.Add("nullable");
        if (IsNull) parts.Add("is_null");
        if (Example) parts.Add("example");
        if (Deprecated) parts.Add("deprecated");
        if (AllowEmpty) parts.Add("allow_empty");
        if (Unique) parts.Add("unique");
        if (Location != null) parts.Add($"location={Location}");
        if (Version != null) parts.Add($"version={Version}");
        if (Mime != null) parts.Add($"mime={Mime}");
        if (ChildType != MmValueType.Unknown) parts.Add($"child_type={ChildType.ToTypeString()}");
        if (ChildDesc != null) parts.Add($"child_desc={ChildDesc}");
        if (ChildMin != null) parts.Add($"child_min={ChildMin}");
        if (ChildMax != null) parts.Add($"child_max={ChildMax}");
        if (ChildPattern != null) parts.Add($"child_pattern={ChildPattern}");
        if (ChildEnums != null) parts.Add($"child_enums={ChildEnums}");
        if (ChildNullable) parts.Add("child_nullable");
        if (ChildAllowEmpty) parts.Add("child_allow_empty");
        if (ChildUnique) parts.Add("child_unique");
        if (ChildLocation != null) parts.Add($"child_location={ChildLocation}");
        if (ChildVersion != null) parts.Add($"child_version={ChildVersion}");
        if (ChildMime != null) parts.Add($"child_mime={ChildMime}");
        return string.Join("; ", parts);
    }

    public byte[] ToBytes()
    {
        return System.Text.Encoding.UTF8.GetBytes(ToTagString());
    }

    public static Tag FromBytes(byte[] data)
    {
        return Parse(System.Text.Encoding.UTF8.GetString(data));
    }

    private static string[] SplitTagString(string s)
    {
        var parts = new List<string>();
        int start = 0;
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '(' || c == '[' || c == '{') depth++;
            else if (c == ')' || c == ']' || c == '}') depth--;
            else if (c == ';' && depth == 0)
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }
        }
        if (start < s.Length)
            parts.Add(s[start..]);
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
    }
}
