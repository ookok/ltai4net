namespace LTAI.Mm.Ir;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class MMAttribute : Attribute
{
    public string TagString { get; }

    private Tag? _parsed;

    public Tag Parsed => _parsed ??= Tag.Parse(TagString);

    public bool IsExcluded => TagString == "-";

    public MMAttribute(string tagString)
    {
        TagString = tagString;
    }
}
